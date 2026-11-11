namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open SI.Rosetta.Common
open SI.Rosetta.Projections
open SI.Rosetta.Projections.TestKit

[<CLIMutable>]
type Tally = { Id: string; Count: int }

/// The TestKit's store as a projection's unit of work, with commits that can be made to fail
type ProjectionUnitOfWorkTests() =
    let Store = TestKitProjectionStore()
    let Commits = List<uint64>()
    let StoredCheckpoint = ref 0UL
    // Some true: the commit is written but its reply is lost; Some false: it fails before writing
    let FailNextCommit = ref None

    // The TestKit's own transaction, so the store writes in it, committing through a failure when one is set
    let UnitOfWork =
        { new IProjectionsUnitOfWork with
            member _.PrepareAsync(_) = Task.CompletedTask
            member _.ClearAsync(_) = Task.CompletedTask
            member _.BeginAsync() =
                let commit changes (checkpoint: Checkpoint) =
                    match FailNextCommit.Value with
                    | Some false ->
                        FailNextCommit.Value <- None
                        raise (TimeoutException "store unavailable")
                    | written ->
                        Store.Apply changes
                        Commits.Add checkpoint.Value
                        StoredCheckpoint.Value <- checkpoint.Value
                        if written = Some true then
                            FailNextCommit.Value <- None
                            raise (TimeoutException "commit reply lost")
                Task.FromResult(new TestKitProjectionTransaction(commit) :> IProjectionTransaction) }

    let CheckpointStore =
        { new ICheckpointStore with
            member _.ReadAsync(id) = Task.FromResult(Checkpoint(Id = id, Value = StoredCheckpoint.Value))
            member _.StoreAsync(_) = failwith "a projection with a unit of work commits its checkpoint" }

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
            UnitOfWork = UnitOfWork,
            Handlers = (handlers :> seq<_>))

    let Documents = Store :> IProjectionsStore

    let LoadTally (id: string) : Task<Tally> = Documents.LoadAsync<Tally>(id)

    /// Adds one to the tally, as an analytics read model would
    let Count () =
        task {
            let! tally = LoadTally "Tallies-1"
            let count = if isNull (box tally) then 0 else tally.Count
            do! Documents.StoreAsync({ Tally.Id = "Tallies-1"; Count = count + 1 })
        } :> Task

    [<Fact>]
    member this.``Every handler's writes are committed with the checkpoint, at once``() =
        task {
            let projection =
                CreateProjection [
                    Handler (fun () -> Documents.StoreAsync({ Tally.Id = "Tallies-1"; Count = 1 }))
                    Handler (fun () -> Documents.StoreAsync({ Tally.Id = "Tallies-2"; Count = 2 })) ]

            do! projection.ProjectAsync(Registered, 3UL)

            Assert.Equal<uint64>([ 3UL ], Commits)
            let! first = LoadTally "Tallies-1"
            let! second = LoadTally "Tallies-2"
            Assert.Equal({ Tally.Id = "Tallies-1"; Count = 1 }, first)
            Assert.Equal({ Tally.Id = "Tallies-2"; Count = 2 }, second)
            Assert.Equal(3UL, projection.Checkpoint.Value)
        }

    [<Fact>]
    member this.``A handler sees what the handlers before it wrote``() =
        task {
            let seen = ref 0
            let projection =
                CreateProjection [
                    Handler Count
                    Handler (fun () ->
                        task {
                            let! tally = LoadTally "Tallies-1"
                            seen.Value <- tally.Count
                        } :> Task) ]

            do! projection.ProjectAsync(Registered, 3UL)

            Assert.Equal(1, seen.Value)
        }

    [<Fact>]
    member this.``When a handler fails, the handlers before it wrote nothing``() =
        task {
            let projection =
                CreateProjection [
                    Handler Count
                    Handler (fun () -> task { failwith "handler failure" } :> Task) ]

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered, 3UL) :> Task)

            Assert.Empty(Commits)
            let! tally = LoadTally "Tallies-1"
            Assert.True(isNull (box tally))
            Assert.Equal(0UL, projection.Checkpoint.Value)
        }

    [<Fact>]
    member this.``An event retried after a handler failed is counted once``() =
        task {
            let failed = ref false
            let projection =
                CreateProjection [
                    Handler Count
                    Handler (fun () ->
                        if failed.Value then Task.CompletedTask
                        else
                            failed.Value <- true
                            Task.FromException(TimeoutException "a service the handler calls is down")) ]

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered, 3UL) :> Task)
            do! projection.ProjectAsync(Registered, 3UL)

            let! tally = LoadTally "Tallies-1"
            Assert.Equal(1, tally.Count)
        }

    [<Fact>]
    member this.``A handler loads what its event stored and deleted before``() =
        task {
            do! Documents.StoreAsync({ Tally.Id = "Tallies-2"; Count = 2 })
            let loaded = List<obj>()
            let projection =
                CreateProjection [
                    Handler (fun () ->
                        task {
                            do! Documents.StoreAsync({ Tally.Id = "Tallies-1"; Count = 1 })
                            do! Documents.DeleteAsync<Tally>("Tallies-2")
                            let! stored = LoadTally "Tallies-1"
                            let! deleted = LoadTally "Tallies-2"
                            loaded.Add(box stored)
                            loaded.Add(box deleted)
                        } :> Task) ]

            do! projection.ProjectAsync(Registered, 3UL)

            Assert.Equal<obj list>([ box { Tally.Id = "Tallies-1"; Count = 1 }; null ], List.ofSeq loaded)
            let! deleted = LoadTally "Tallies-2"
            Assert.True(isNull (box deleted))
        }

    [<Fact>]
    member this.``An event whose commit failed but was written isn't handled again``() =
        task {
            FailNextCommit.Value <- Some true
            let projection = CreateProjection [ Handler Count ]

            let! _ = Assert.ThrowsAsync<TimeoutException>(fun () -> projection.ProjectAsync(Registered, 3UL) :> Task)
            do! projection.ProjectAsync(Registered, 3UL)

            let! tally = LoadTally "Tallies-1"
            Assert.Equal(1, tally.Count)
            Assert.Equal<uint64>([ 3UL ], Commits)
            Assert.Equal(3UL, projection.Checkpoint.Value)
        }

    [<Fact>]
    member this.``An event whose commit failed and wasn't written is handled again``() =
        task {
            FailNextCommit.Value <- Some false
            let projection = CreateProjection [ Handler Count ]

            let! _ = Assert.ThrowsAsync<TimeoutException>(fun () -> projection.ProjectAsync(Registered, 3UL) :> Task)
            do! projection.ProjectAsync(Registered, 3UL)

            let! tally = LoadTally "Tallies-1"
            Assert.Equal(1, tally.Count)
            Assert.Equal<uint64>([ 3UL ], Commits)
        }

    [<Fact>]
    member this.``Outside a projection, the store writes straight away``() =
        task {
            do! Documents.StoreAsync({ Tally.Id = "Tallies-1"; Count = 1 })

            let! tally = LoadTally "Tallies-1"
            Assert.Equal(1, tally.Count)
        }

[<HandlesStream(OrganizationStream); DisabledProjection; ProjectionVersion(2)>]
type VersionedOrganizationProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<OrganizationProjectionHandler, OrganizationEvents>
    interface IProjectInto<Organization>

/// [<ProjectionVersion>]: a projection whose version went up is rebuilt when it starts
type ProjectionVersionTests() =
    let Stored = Dictionary<string, Checkpoint>()
    let Cleared = List<string>()

    let Services =
        let store = TestKitProjectionStore()
        ServiceCollection()
            .AddSingleton<INoSqlStore>(store)
            .AddSingleton<IProjectionsUnitOfWork>(
                { new IProjectionsUnitOfWork with
                    member _.PrepareAsync(_) = Task.CompletedTask
                    member _.BeginAsync() = (store :> IProjectionsUnitOfWork).BeginAsync()
                    member _.ClearAsync(readModels) =
                        Cleared.AddRange(readModels |> Array.map (fun readModel -> readModel.Name))
                        Task.CompletedTask })
            .AddSingleton<ICheckpointStore>(
                { new ICheckpointStore with
                    member _.ReadAsync(id) =
                        Task.FromResult(
                            match Stored.TryGetValue id with
                            | true, checkpoint -> checkpoint
                            | false, _ -> Checkpoint(Id = id))
                    member _.StoreAsync(checkpoint) =
                        Stored.[checkpoint.Id] <- checkpoint
                        Task.CompletedTask })
            .AddTransient<ISubscriptionFactory, TestKitSubscriptionFactory>()
            .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
            .AddTransient<IProjectionsFactory, ProjectionsFactory>()
            .AddTransient<OrganizationProjectionHandler>()
            .BuildServiceProvider()

    let Create () =
        Services.GetRequiredService<IProjectionsFactory>().CreateAsync(typeof<VersionedOrganizationProjection>)

    let StoredAt (value: uint64) (version: int) =
        Stored.["Checkpoints-VersionedOrganization"] <- Checkpoint(Id = "Checkpoints-VersionedOrganization", Value = value, Version = version)

    [<Fact>]
    member this.``A projection whose version went up empties its read models and starts from the beginning``() =
        task {
            StoredAt 42UL 1

            let! projection = Create ()

            Assert.Equal<string>([ "Organization" ], Cleared)
            let stored = Stored.["Checkpoints-VersionedOrganization"]
            Assert.Equal((0UL, 2), (stored.Value, stored.Version))
            Assert.Equal((0UL, 2), (projection.Checkpoint.Value, projection.Checkpoint.Version))
        }

    [<Fact>]
    member this.``A projection at its version continues from its checkpoint``() =
        task {
            StoredAt 42UL 2

            let! projection = Create ()

            Assert.Empty(Cleared)
            Assert.Equal(42UL, projection.Checkpoint.Value)
        }

    [<Fact>]
    member this.``A projection older than its read models doesn't start``() =
        task {
            StoredAt 42UL 3

            let! ex = Assert.ThrowsAsync<InvalidOperationException>(fun () -> Create () :> Task)

            Assert.Contains("version 3", ex.Message)
            Assert.Empty(Cleared)
        }

    interface IDisposable with
        member _.Dispose() = Services.Dispose()

/// Subscriptions whose stream says the read models built up to a checkpoint no longer match it, from checkpoint on
type ReplacingSubscriptionFactory(replacedUpTo: uint64) =
    interface ISubscriptionFactory with
        member _.Create<'TEvent when 'TEvent :> IAggregateEvents>() =
            let mutable name = ""
            let mutable stream = ""
            let mutable received: 'TEvent * uint64 -> Task<unit> = fun _ -> Task.FromResult()
            let mutable skip = false
            { new ISubscription<'TEvent> with
                member _.Name with get () = name and set value = name <- value
                member _.StreamName with get () = stream and set value = stream <- value
                member _.StartAsync(_, _) = Task.CompletedTask
                member _.EventReceived with get () = received and set value = received <- value
                member _.SkipUnknownEvents with get () = skip and set value = skip <- value
              interface ISubscriptionHistory with
                member _.ReplacedSinceAsync(checkpoint) =
                    Task.FromResult(if checkpoint > 0UL && checkpoint <= replacedUpTo then Some "cp-Organizations was linked again" else None) }

/// A projection whose stream was replaced under it (a custom projection's cp- stream linked again) is rebuilt
type ReplacedStreamTests() =
    let Stored = Dictionary<string, Checkpoint>()
    let Cleared = List<string>()

    let Services (replacedUpTo: uint64) =
        let store = TestKitProjectionStore()
        ServiceCollection()
            .AddSingleton<INoSqlStore>(store)
            .AddSingleton<IProjectionsUnitOfWork>(
                { new IProjectionsUnitOfWork with
                    member _.PrepareAsync(_) = Task.CompletedTask
                    member _.BeginAsync() = (store :> IProjectionsUnitOfWork).BeginAsync()
                    member _.ClearAsync(readModels) =
                        Cleared.AddRange(readModels |> Array.map (fun readModel -> readModel.Name))
                        Task.CompletedTask })
            .AddSingleton<ICheckpointStore>(
                { new ICheckpointStore with
                    member _.ReadAsync(id) =
                        Task.FromResult(
                            match Stored.TryGetValue id with
                            | true, checkpoint -> checkpoint
                            | false, _ -> Checkpoint(Id = id))
                    member _.StoreAsync(checkpoint) =
                        Stored.[checkpoint.Id] <- checkpoint
                        Task.CompletedTask })
            .AddSingleton<ISubscriptionFactory>(ReplacingSubscriptionFactory(replacedUpTo))
            .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
            .AddTransient<IProjectionsFactory, ProjectionsFactory>()
            .AddTransient<OrganizationProjectionHandler>()
            .BuildServiceProvider()

    let Create (replacedUpTo: uint64) =
        task {
            use services = Services replacedUpTo
            return! services.GetRequiredService<IProjectionsFactory>().CreateAsync(typeof<VersionedOrganizationProjection>)
        }

    let StoredAt (value: uint64) =
        Stored.["Checkpoints-VersionedOrganization"] <- Checkpoint(Id = "Checkpoints-VersionedOrganization", Value = value, Version = 2)

    [<Fact>]
    member this.``A projection built from links its stream no longer holds empties its read models and starts from the beginning``() =
        task {
            StoredAt 42UL

            let! projection = Create 42UL

            Assert.Equal<string>([ "Organization" ], Cleared)
            Assert.Equal((0UL, 2), (projection.Checkpoint.Value, projection.Checkpoint.Version))
            Assert.Equal(0UL, Stored.["Checkpoints-VersionedOrganization"].Value)
        }

    [<Fact>]
    member this.``A projection past the replacement continues from its checkpoint``() =
        task {
            StoredAt 43UL

            let! projection = Create 42UL

            Assert.Empty(Cleared)
            Assert.Equal(43UL, projection.Checkpoint.Value)
        }
