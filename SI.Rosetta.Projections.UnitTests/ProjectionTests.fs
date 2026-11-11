namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Xunit
open SI.Rosetta.Projections

type ProjectionTests() =
    let StoredCheckpoints = List<uint64>()

    let CheckpointStore =
        { new ICheckpointStore with
            member _.ReadAsync(id) = Task.FromResult(Checkpoint(Id = id))
            member _.StoreAsync(checkpoint) =
                StoredCheckpoints.Add checkpoint.Value
                Task.CompletedTask }

    let Handler (handle: unit -> Task) =
        { new IProjectionHandler<OrganizationEvents> with
            member _.Handle(_, _) = handle () }

    let Registered = OrganizationEvents.Registered { Id = "Organizations-1"; Name = "Rosetta"; DateRegistered = DateTime.MinValue }

    let CreateProjection (handlers: IProjectionHandler<OrganizationEvents> list) =
        Projection<OrganizationEvents>(
            Name = "Organization",
            SubscriptionStreamName = OrganizationStream,
            Checkpoint = Checkpoint(Id = "Checkpoints-Organization"),
            CheckpointStore = CheckpointStore,
            Handlers = (handlers :> seq<_>))

    [<Fact>]
    member this.``Runs the handlers one at a time, in order, then stores the checkpoint``() =
        task {
            let handled = List<string>()
            let projection =
                CreateProjection [
                    Handler (fun () ->
                        task {
                            do! Task.Yield()
                            handled.Add "first"
                        } :> Task)
                    Handler (fun () -> task { handled.Add "second" } :> Task) ]

            do! projection.ProjectAsync(Registered, 3UL)

            Assert.Equal<string>([ "first"; "second" ], handled)
            Assert.Equal<uint64>([ 3UL ], StoredCheckpoints)
        }

    [<Fact>]
    member this.``The first handler that fails stops the event, and the checkpoint isn't stored``() =
        task {
            let ran = List<string>()
            let projection =
                CreateProjection [
                    Handler (fun () -> task { ran.Add "first" } :> Task)
                    // Throws before it returns a Task
                    Handler (fun () -> raise (InvalidOperationException "sync failure"))
                    Handler (fun () -> task { ran.Add "third" } :> Task) ]

            let! ex = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered, 3UL) :> Task)

            let failure = Assert.Single((ex.InnerException :?> AggregateException).InnerExceptions)
            Assert.Equal("sync failure", failure.Message)
            Assert.Equal<string>([ "first" ], ran)
            Assert.Equal("Organization", ex.ProjectionName)
            Assert.Equal(3UL, ex.Checkpoint)
            Assert.Empty(StoredCheckpoints)
        }
