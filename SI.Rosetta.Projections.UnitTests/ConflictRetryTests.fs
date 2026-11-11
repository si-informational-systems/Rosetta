namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open MongoDB.Driver
open Npgsql
open Raven.Client.Documents
open Xunit
open SI.Rosetta.Common
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Projections
open SI.Rosetta.Projections.MongoDB
open SI.Rosetta.Projections.PostgreSQL
open SI.Rosetta.Projections.RavenDB
open SI.Rosetta.Projections.TestKit

/// A store's own conflict, as a unit of work recognizes it
type StoreConflictException() =
    inherit Exception("write conflict")

/// An event that fails on a conflict is retried at once, in a new transaction, before its subscription backs off
type ConflictRetryTests() =
    let Store = TestKitProjectionStore()
    let Commits = List<uint64>()
    // How many commits to fail with the store's own conflict
    let mutable FailingCommits = 0

    let UnitOfWork =
        { new IProjectionsUnitOfWork with
            member _.PrepareAsync(_) = Task.CompletedTask
            member _.ClearAsync(_) = Task.CompletedTask
            member _.BeginAsync() =
                let commit changes (checkpoint: Checkpoint) =
                    if FailingCommits > 0 then
                        FailingCommits <- FailingCommits - 1
                        raise (StoreConflictException())
                    Store.Apply changes
                    Commits.Add checkpoint.Value
                Task.FromResult(new TestKitProjectionTransaction(commit) :> IProjectionTransaction)
          interface IRecognizeConflicts with
            member _.IsConflict(ex) = ex :? StoreConflictException }

    let Registered = OrganizationEvents.Registered { Id = "Organizations-1"; Name = "Rosetta"; DateRegistered = DateTime.MinValue }

    let Documents = Store :> IProjectionsStore

    /// Counts the tally up, after failing with failure the first failures times
    let CountingHandler (failures: int) (failure: unit -> exn) (attempts: int ref) =
        { new IProjectionHandler<OrganizationEvents> with
            member _.Handle(_, _) =
                task {
                    attempts.Value <- attempts.Value + 1
                    if attempts.Value <= failures then raise (failure ())
                    let! tally = Documents.LoadAsync<Tally>("Tallies-1")
                    let count = if isNull (box tally) then 0 else tally.Count
                    do! Documents.StoreAsync({ Tally.Id = "Tallies-1"; Count = count + 1 })
                } :> Task }

    let Projection (handler: IProjectionHandler<OrganizationEvents>) (retries: ConflictRetries) =
        Projection<OrganizationEvents>(
            Name = "Organization",
            SubscriptionStreamName = OrganizationStream,
            Checkpoint = Checkpoint(Id = "Checkpoints-Organization"),
            CheckpointStore = { new ICheckpointStore with
                                  member _.ReadAsync(id) = Task.FromResult(Checkpoint(Id = id, Value = (if Commits.Count = 0 then 0UL else Commits.[Commits.Count - 1])))
                                  member _.StoreAsync(_) = Task.CompletedTask },
            UnitOfWork = UnitOfWork,
            Handlers = [ handler ],
            ConflictRetries = retries)

    let Tally () =
        task {
            let! tally = Documents.LoadAsync<Tally>("Tallies-1")
            return if isNull (box tally) then 0 else tally.Count
        }

    let Conflict () = ConcurrencyException "Another command stored it first" :> exn

    [<Fact>]
    member this.``A handler's conflict is retried at once, and the event counted once``() =
        task {
            let attempts = ref 0
            let projection = Projection (CountingHandler 2 Conflict attempts) ConflictRetries.Default

            do! projection.HandleEvent(Registered, 1UL)

            Assert.Equal(3, attempts.Value)
            let! tally = Tally ()
            Assert.Equal(1, tally)
            Assert.Equal<uint64>([ 1UL ], Commits)
        }

    [<Fact>]
    member this.``A store's own conflict is retried at once``() =
        task {
            FailingCommits <- 1
            let attempts = ref 0
            let projection = Projection (CountingHandler 0 Conflict attempts) ConflictRetries.Default

            do! projection.HandleEvent(Registered, 1UL)

            Assert.Equal(2, attempts.Value)
            let! tally = Tally ()
            Assert.Equal(1, tally)
        }

    [<Fact>]
    member this.``A conflict that outlasts the retries fails the event, and nothing is written``() =
        task {
            let attempts = ref 0
            let projection = Projection (CountingHandler 10 Conflict attempts) { Retries = 3; MaxJitter = TimeSpan.FromMilliseconds 5.0 }

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.HandleEvent(Registered, 1UL) :> Task)

            Assert.Equal(4, attempts.Value)
            Assert.Empty(Commits)
        }

    [<Fact>]
    member this.``Any other failure isn't retried at once``() =
        task {
            let attempts = ref 0
            let projection = Projection (CountingHandler 1 (fun () -> InvalidOperationException "a bug" :> exn) attempts) ConflictRetries.Default

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.HandleEvent(Registered, 1UL) :> Task)

            Assert.Equal(1, attempts.Value)
        }

    [<Fact>]
    member this.``Without retries, a conflict fails at once``() =
        task {
            let attempts = ref 0
            let projection = Projection (CountingHandler 1 Conflict attempts) ConflictRetries.None

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.HandleEvent(Registered, 1UL) :> Task)

            Assert.Equal(1, attempts.Value)
        }

    [<Fact>]
    member this.``Each store recognizes its own conflicts``() =
        use client = new MongoClient("mongodb://localhost")
        let mongo = MongoDbProjectionsUnitOfWork(client, client.GetDatabase("Rosetta")) :> IRecognizeConflicts
        let writeConflict = MongoException("WriteConflict")
        writeConflict.AddErrorLabel "TransientTransactionError"
        use database = new PostgreSqlDatabase({ ConnectionString = "Host=localhost;Database=Rosetta"; Schema = "public" })
        let postgres = PostgreSqlProjectionsUnitOfWork(database) :> IRecognizeConflicts
        use store = new DocumentStore()
        let raven = RavenDbProjectionsUnitOfWork(store) :> IRecognizeConflicts

        Assert.True(mongo.IsConflict writeConflict)
        Assert.False(mongo.IsConflict(MongoException "duplicate key"))
        Assert.True(postgres.IsConflict(PostgresException("deadlock detected", "ERROR", "ERROR", PostgresErrorCodes.DeadlockDetected)))
        Assert.True(postgres.IsConflict(PostgresException("could not serialize access", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure)))
        Assert.False(postgres.IsConflict(PostgresException("syntax error", "ERROR", "ERROR", PostgresErrorCodes.SyntaxError)))
        Assert.True(raven.IsConflict(Raven.Client.Exceptions.ConcurrencyException("changed meanwhile")))
        Assert.False(raven.IsConflict(TimeoutException()))

    [<Fact>]
    member this.``The application's ConflictRetries applies to its projections``() =
        task {
            let retries = { Retries = 7; MaxJitter = TimeSpan.FromMilliseconds 9.0 }
            use services =
                ServiceCollection()
                    .AddSingleton<INoSqlStore>(Store)
                    .AddSingleton<IProjectionsUnitOfWork>(Store)
                    .AddSingleton<ICheckpointStore>(
                        { new ICheckpointStore with
                            member _.ReadAsync(id) = Task.FromResult(Checkpoint(Id = id, Version = 2))
                            member _.StoreAsync(_) = Task.CompletedTask })
                    .AddSingleton<ConflictRetries>(retries)
                    .AddTransient<ISubscriptionFactory, TestKitSubscriptionFactory>()
                    .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
                    .AddTransient<IProjectionsFactory, ProjectionsFactory>()
                    .AddTransient<OrganizationProjectionHandler>()
                    .BuildServiceProvider()

            let! projection = services.GetRequiredService<IProjectionsFactory>().CreateAsync(typeof<VersionedOrganizationProjection>)

            Assert.Equal(retries, (projection :?> Projection<OrganizationEvents>).ConflictRetries)
        }
