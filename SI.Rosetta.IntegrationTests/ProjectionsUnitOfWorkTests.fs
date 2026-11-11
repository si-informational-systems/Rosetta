namespace SI.Rosetta.IntegrationTests

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Xunit
open SI.Rosetta.Persistence.MongoDB
open SI.Rosetta.Persistence.RavenDB
open SI.Rosetta.Projections
open SI.Rosetta.Projections.MongoDB
open SI.Rosetta.Projections.RavenDB

/// How many events a Parcel had: a read model that adds one for each
[<CLIMutable>]
type ParcelTally = { Id: string; Count: int }

/// One store's read model store, checkpoint store and unit of work, and the projection type its events are begun for
type StoreUnderTest =
    { Store: IProjectionsStore
      Checkpoints: ICheckpointStore
      UnitOfWork: IProjectionsUnitOfWork
      ProjectionType: Type }

/// A projection whose events are handled in RavenDB cluster-wide transactions
[<ClusterWideTransaction; DisabledProjection; HandlesStream(ParcelStreams.Parcels)>]
type ClusterWideUnitOfWorkProjection() =
    inherit Projection<ParcelEvents>()

/// What the document stores' transactions guarantee, against the real databases (PostgreSQL: PostgreSqlProjectionsTests)
type ProjectionsUnitOfWorkTests(databases: Databases) =
    let Disposables = List<IDisposable>()

    let Registered = ParcelEvents.Registered { Id = "Parcels-1"; Recipient = "Ada" }

    let NewId (prefix: string) = sprintf "%s-%s" prefix (Guid.NewGuid().ToString("N"))

    let Handler (handle: unit -> Task) =
        { new IProjectionHandler<ParcelEvents> with
            member _.Handle(_, _) = handle () }

    /// The store, prepared as when the projections start
    member private this.Create(storeName: string) =
        task {
            let store = this.Stores storeName
            do! store.UnitOfWork.PrepareAsync(store.ProjectionType, [||])
            return store
        }

    member private this.Stores(storeName: string) =
        match storeName with
        | "RavenDB" | "RavenDB cluster-wide" ->
            let documentStore =
                RavenDocumentStoreFactory.CreateAndInitializeDocumentStore
                    (RavenConfig.FromConfiguration(databases.Configuration "Rosetta-ProjectionsUnitOfWorkTests")) None
            Disposables.Add documentStore
            { Store = RavenDbProjectionsStore(documentStore)
              Checkpoints = RavenDbCheckpointStore(documentStore)
              UnitOfWork = RavenDbProjectionsUnitOfWork(documentStore)
              ProjectionType = (if storeName = "RavenDB" then null else typeof<ClusterWideUnitOfWorkProjection>) }
        | "MongoDB" ->
            MongoConventions.Register()
            let client = new MongoClient(databases.MongoReplicaSetUrl)
            Disposables.Add client
            let database = client.GetDatabase("Rosetta-ProjectionsUnitOfWorkTests")
            { Store = MongoDbProjectionsStore(client, database)
              Checkpoints = MongoDbCheckpointStore(database)
              UnitOfWork = MongoDbProjectionsUnitOfWork(client, database)
              ProjectionType = null }
        | _ -> invalidArg (nameof storeName) storeName

    /// A projection with its own checkpoint, prepared as ProjectionsFactory prepares it
    member private this.Projection(store: StoreUnderTest, handlers: IProjectionHandler<ParcelEvents> list) =
        task {
            let name = NewId "UnitOfWork"
            let! checkpoint = store.Checkpoints.ReadAsync(sprintf "Checkpoints-%s" name)
            return
                Projection<ParcelEvents>(
                    Name = name,
                    SubscriptionStreamName = ParcelStreams.Parcels,
                    Checkpoint = checkpoint,
                    CheckpointStore = store.Checkpoints,
                    UnitOfWork = store.UnitOfWork,
                    ProjectionType = store.ProjectionType,
                    Handlers = (handlers :> seq<_>))
        }

    interface IDisposable with
        member _.Dispose() =
            for disposable in Disposables do
                disposable.Dispose()

    [<Theory; InlineData("RavenDB"); InlineData("RavenDB cluster-wide"); InlineData("MongoDB")>]
    member this.``An event's writes and its checkpoint are committed together``(storeName: string) =
        task {
            let! store = this.Create storeName
            let kept = { Id = NewId "Parcels"; Recipient = "Ada" } : Parcel
            let removed = { Id = NewId "Parcels"; Recipient = "Grace" } : Parcel
            do! store.Store.StoreAsync(removed)
            let routeId = NewId "Routes"
            let! projection =
                this.Projection(store, [
                    Handler (fun () -> store.Store.StoreAsync(kept))
                    Handler (fun () ->
                        task {
                            do! store.Store.DeleteAsync<Parcel>(removed.Id)
                            do! store.Store.StoreManyAsync([| ({ Id = routeId; Recipients = [ "Ada" ] } : ParcelRoute) |])
                        }) ])

            do! projection.ProjectAsync(Registered, 5UL)

            let! storedKept = store.Store.LoadAsync<Parcel>(kept.Id)
            let! storedRemoved = store.Store.LoadAsync<Parcel>(removed.Id)
            let! route = store.Store.LoadAsync<ParcelRoute>(routeId)
            let! checkpoint = store.Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.Equal(kept, storedKept)
            Assert.True(isNull (box storedRemoved))
            Assert.Equal<string list>([ "Ada" ], route.Recipients)
            Assert.Equal(5UL, checkpoint.Value)
        }

    [<Theory; InlineData("RavenDB"); InlineData("RavenDB cluster-wide"); InlineData("MongoDB")>]
    member this.``When a handler fails, nothing is written and the checkpoint stays``(storeName: string) =
        task {
            let! store = this.Create storeName
            let parcel = { Id = NewId "Parcels"; Recipient = "Ada" } : Parcel
            let! projection =
                this.Projection(store, [
                    Handler (fun () -> store.Store.StoreAsync(parcel))
                    Handler (fun () -> task { failwith "handler failure" }) ])

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered, 5UL) :> Task)

            let! stored = store.Store.LoadAsync<Parcel>(parcel.Id)
            let! checkpoint = store.Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.True(isNull (box stored))
            Assert.Equal(0UL, checkpoint.Value)
        }

    [<Theory; InlineData("RavenDB"); InlineData("RavenDB cluster-wide"); InlineData("MongoDB")>]
    member this.``An event retried after a handler failed adds one only once``(storeName: string) =
        task {
            let! store = this.Create storeName
            let tallyId = NewId "ParcelTallies"
            let failed = ref false
            let! projection =
                this.Projection(store, [
                    Handler (fun () ->
                        task {
                            let! tally = store.Store.LoadAsync<ParcelTally>(tallyId)
                            let count = if isNull (box tally) then 0 else tally.Count
                            do! store.Store.StoreAsync({ Id = tallyId; Count = count + 1 })
                        })
                    Handler (fun () ->
                        if failed.Value then Task.CompletedTask
                        else
                            failed.Value <- true
                            Task.FromException(TimeoutException "a service the handler calls is down")) ])

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered, 5UL) :> Task)
            do! projection.ProjectAsync(Registered, 5UL)

            let! tally = store.Store.LoadAsync<ParcelTally>(tallyId)
            Assert.Equal(1, tally.Count)
        }

    [<Theory; InlineData("RavenDB"); InlineData("RavenDB cluster-wide"); InlineData("MongoDB")>]
    member this.``A handler loads what its event stored and deleted, before the commit``(storeName: string) =
        task {
            let! store = this.Create storeName
            let stored = { Id = NewId "Parcels"; Recipient = "Ada" } : Parcel
            let deleted = { Id = NewId "Parcels"; Recipient = "Grace" } : Parcel
            do! store.Store.StoreAsync(deleted)
            let loaded = List<obj>()
            let! projection =
                this.Projection(store, [
                    Handler (fun () ->
                        task {
                            do! store.Store.StoreAsync(stored)
                            do! store.Store.DeleteAsync<Parcel>(deleted.Id)
                            let! many = store.Store.LoadManyAsync<Parcel>([| box stored.Id; box deleted.Id |])
                            loaded.Add(box many.[stored.Id])
                            loaded.Add(box many.[deleted.Id])
                            // Not written yet: outside the event, the store still has it
                            let outside =
                                use _ = ExecutionContext.SuppressFlow()
                                Task.Run<Parcel>(Func<Task<Parcel>>(fun () -> store.Store.LoadAsync<Parcel>(deleted.Id)))
                            let! fromStore = outside
                            loaded.Add(box fromStore)
                        }) ])

            do! projection.ProjectAsync(Registered, 5UL)

            Assert.Equal<obj list>([ box stored; null; box deleted ], List.ofSeq loaded)
        }

    [<Theory; InlineData("RavenDB"); InlineData("RavenDB cluster-wide"); InlineData("MongoDB")>]
    member this.``A handler stores a changed copy of a document it loaded``(storeName: string) =
        task {
            let! store = this.Create storeName
            let parcel = { Id = NewId "Parcels"; Recipient = "Ada" } : Parcel
            do! store.Store.StoreAsync(parcel)
            let! projection =
                this.Projection(store, [
                    Handler (fun () ->
                        task {
                            let! loaded = store.Store.LoadAsync<Parcel>(parcel.Id)
                            // A new record with the loaded one's Id: RavenDB's session alone would throw NonUniqueObjectException
                            do! store.Store.StoreAsync({ loaded with Recipient = "Grace" })
                        }) ])

            do! projection.ProjectAsync(Registered, 5UL)

            let! stored = store.Store.LoadAsync<Parcel>(parcel.Id)
            Assert.Equal("Grace", stored.Recipient)
        }

    [<Theory; InlineData("RavenDB"); InlineData("RavenDB cluster-wide"); InlineData("MongoDB")>]
    member this.``A handler deletes a document and stores it again``(storeName: string) =
        task {
            let! store = this.Create storeName
            let parcel = { Id = NewId "Parcels"; Recipient = "Ada" } : Parcel
            let unloaded = { Id = NewId "Parcels"; Recipient = "Ada" } : Parcel
            do! store.Store.StoreManyAsync([| parcel; unloaded |])
            let! projection =
                this.Projection(store, [
                    Handler (fun () ->
                        task {
                            let! _ = store.Store.LoadAsync<Parcel>(parcel.Id)
                            do! store.Store.DeleteAsync<Parcel>(parcel.Id)
                            do! store.Store.StoreAsync({ parcel with Recipient = "Grace" })
                            // Never loaded in the event
                            do! store.Store.DeleteAsync<Parcel>(unloaded.Id)
                            do! store.Store.StoreAsync({ unloaded with Recipient = "Grace" })
                        }) ])

            do! projection.ProjectAsync(Registered, 5UL)

            let! stored = store.Store.LoadManyAsync<Parcel>([| box parcel.Id; box unloaded.Id |])
            Assert.Equal("Grace", stored.[parcel.Id].Recipient)
            Assert.Equal("Grace", stored.[unloaded.Id].Recipient)
        }

    [<Theory; InlineData("RavenDB"); InlineData("RavenDB cluster-wide"); InlineData("MongoDB")>]
    member this.``A handler sees what the handler before it wrote``(storeName: string) =
        task {
            let! store = this.Create storeName
            let parcel = { Id = NewId "Parcels"; Recipient = "Ada" } : Parcel
            let seen = ref ""
            let! projection =
                this.Projection(store, [
                    Handler (fun () -> store.Store.StoreAsync(parcel))
                    Handler (fun () ->
                        task {
                            let! stored = store.Store.LoadAsync<Parcel>(parcel.Id)
                            seen.Value <- stored.Recipient
                        }) ])

            do! projection.ProjectAsync(Registered, 5UL)

            Assert.Equal("Ada", seen.Value)
        }

    [<Fact>]
    member this.``MongoDB projections don't start on a standalone server``() =
        task {
            use client = new MongoClient(databases.MongoStandaloneUrl)
            let unitOfWork = MongoDbProjectionsUnitOfWork(client, client.GetDatabase("Rosetta-ProjectionsUnitOfWorkTests")) :> IProjectionsUnitOfWork

            let! ex = Assert.ThrowsAsync<InvalidOperationException>(fun () -> unitOfWork.PrepareAsync(null, [||]))

            Assert.Contains("replica set", ex.Message)
        }
