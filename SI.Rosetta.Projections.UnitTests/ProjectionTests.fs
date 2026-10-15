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
    member this.``Stores the checkpoint once every handler is done``() =
        task {
            let handled = Collections.Concurrent.ConcurrentBag<string>()
            let projection =
                CreateProjection [
                    Handler (fun () ->
                        task {
                            do! Task.Yield()
                            handled.Add "first"
                        } :> Task)
                    Handler (fun () -> task { handled.Add "second" } :> Task) ]

            do! projection.ProjectAsync(Registered, 3UL)

            Assert.Equal(2, handled.Count)
            Assert.Equal<uint64>([ 3UL ], StoredCheckpoints)
        }

    [<Fact>]
    member this.``Every handler's failure is in the ProjectionException, and the checkpoint isn't stored``() =
        task {
            let projection =
                CreateProjection [
                    Handler (fun () ->
                        task {
                            do! Task.Yield()
                            failwith "async failure"
                        } :> Task)
                    // Throws before it returns a Task
                    Handler (fun () -> raise (InvalidOperationException "sync failure"))
                    Handler (fun () -> Task.CompletedTask) ]

            let! ex = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered, 3UL) :> Task)

            let failures = (ex.InnerException :?> AggregateException).InnerExceptions |> Seq.map (fun e -> e.Message) |> Seq.sort
            Assert.Equal<string>([ "async failure"; "sync failure" ], failures)
            Assert.Equal("Organization", ex.ProjectionName)
            Assert.Equal(3UL, ex.Checkpoint)
            Assert.Empty(StoredCheckpoints)
        }
