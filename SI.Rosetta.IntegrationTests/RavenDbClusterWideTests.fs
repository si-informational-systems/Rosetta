namespace SI.Rosetta.IntegrationTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open MongoDB.Driver
open Raven.Client.Documents
open Raven.Client.Documents.Operations.CompareExchange
open Xunit
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Persistence.RavenDB
open SI.Rosetta.Projections
open SI.Rosetta.Projections.MongoDB
open SI.Rosetta.Projections.PostgreSQL
open SI.Rosetta.Projections.RavenDB

/// What a cluster-wide projection writes: a document per Parcel
[<CLIMutable>]
type ClaimedParcel = { Id: string; Recipient: string; Events: int }

/// A lock a Parcel's projection releases: a compare-exchange value
type ParcelClaim = { Recipient: string }

[<ClusterWideTransaction; DisabledProjection; HandlesStream(ParcelStreams.Parcels)>]
type ClusterWideParcelProjection() =
    inherit Projection<ParcelEvents>()

/// The read model a version bump rebuilds, in version 1 and in version 2
[<ClusterWideTransaction; DisabledProjection; HandlesStream(ParcelStreams.Parcels); ProjectionVersion(1)>]
type ReplayedParcelV1Projection() =
    inherit Projection<ParcelEvents>()
    interface IProjectInto<ClaimedParcel>

[<ClusterWideTransaction; DisabledProjection; HandlesStream(ParcelStreams.Parcels); ProjectionVersion(2)>]
type ReplayedParcelV2Projection() =
    inherit Projection<ParcelEvents>()
    interface IProjectInto<ClaimedParcel>

/// Only a Parcel's registration is cluster-wide: its readdressing is handled in a normal session
[<ClusterWideTransaction(typeof<ParcelRegistered>); DisabledProjection; HandlesStream(ParcelStreams.Parcels)>]
type RegistrationClusterWideParcelProjection() =
    inherit Projection<ParcelEvents>()

/// The same, rebuilt by a version bump
[<ClusterWideTransaction(typeof<ParcelRegistered>); DisabledProjection; HandlesStream(ParcelStreams.Parcels); ProjectionVersion(1)>]
type ReplayedRegistrationV1Projection() =
    inherit Projection<ParcelEvents>()
    interface IProjectInto<ClaimedParcel>

[<ClusterWideTransaction(typeof<ParcelRegistered>); DisabledProjection; HandlesStream(ParcelStreams.Parcels); ProjectionVersion(2)>]
type ReplayedRegistrationV2Projection() =
    inherit Projection<ParcelEvents>()
    interface IProjectInto<ClaimedParcel>

/// [<ClusterWideTransaction>] against RavenDB: an event's documents, the compare-exchange values its handlers change
/// through the event's session, and its checkpoint are one cluster-wide transaction
type RavenDbClusterWideTests(databases: Databases) =
    let Store =
        RavenDocumentStoreFactory.CreateAndInitializeDocumentStore
            (RavenConfig.FromConfiguration(databases.Configuration "Rosetta-ClusterWideTests")) None
    let Documents = RavenDbProjectionsStore(Store) :> IProjectionsStore
    let Checkpoints = RavenDbCheckpointStore(Store) :> ICheckpointStore
    let UnitOfWork = RavenDbProjectionsUnitOfWork(Store) :> IProjectionsUnitOfWork
    /// What rebuilds a projection whose version went up
    let Factory = ProjectionsFactory(ServiceCollection().BuildServiceProvider())

    let NewId (prefix: string) = sprintf "%s-%s" prefix (Guid.NewGuid().ToString("N"))

    let Registered id = ParcelEvents.Registered { Id = id; Recipient = "Ada" }
    let Readdressed id = ParcelEvents.Readdressed { Id = id; Recipient = "Grace" }

    let Handler (handle: ParcelEvents -> Task) =
        { new IProjectionHandler<ParcelEvents> with
            member _.Handle(ev, _) = handle ev }

    /// The session of the event being handled
    let Session () =
        match RavenDbProjectionTransaction.Current with
        | Some session -> session
        | None -> failwith "no RavenDB transaction"

    /// Writes the Parcel's document as the events say: a new record, or a changed copy of the stored one
    let ProjectParcel (ev: ParcelEvents) : Task =
        task {
            match ev with
            | ParcelEvents.Registered e -> do! Documents.StoreAsync({ Id = e.Id; Recipient = e.Recipient; Events = 1 })
            | ParcelEvents.Readdressed e ->
                let! parcel = Documents.LoadAsync<ClaimedParcel>(e.Id)
                do! Documents.StoreAsync({ parcel with Recipient = e.Recipient; Events = parcel.Events + 1 })
        }

    /// Records whether each event's transaction is cluster-wide
    let RecordKind (kinds: List<bool>) =
        Handler (fun _ ->
            match ProjectionTransaction.Current with
            | :? RavenDbProjectionTransaction as transaction -> kinds.Add transaction.ClusterWide
            | _ -> failwith "no RavenDB transaction"
            Task.CompletedTask)

    let PutClaim (key: string) =
        task {
            let! result = Store.Operations.SendAsync(PutCompareExchangeValueOperation<ParcelClaim>(key, { Recipient = "Ada" }, 0L))
            Assert.True(result.Successful)
        }

    let Claim (key: string) =
        Store.Operations.SendAsync(GetCompareExchangeValueOperation<ParcelClaim>(key))

    /// Releases the claim in the event's transaction
    let ReleaseClaim (key: string) : Task =
        task {
            let session = Session ()
            let! claim = session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<ParcelClaim>(key)
            session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(claim)
        }

    member private this.Projection(projectionType: Type, handlers: IProjectionHandler<ParcelEvents> list, ?name: string) =
        task {
            let name = defaultArg name (NewId "ClusterWide")
            do! UnitOfWork.PrepareAsync(projectionType, ProjectionReadModels.Of projectionType)
            let! checkpoint = Checkpoints.ReadAsync(sprintf "Checkpoints-%s" name)
            return
                Projection<ParcelEvents>(
                    Name = name,
                    SubscriptionStreamName = ParcelStreams.Parcels,
                    Checkpoint = checkpoint,
                    CheckpointStore = Checkpoints,
                    UnitOfWork = UnitOfWork,
                    ProjectionType = projectionType,
                    Handlers = (handlers :> seq<_>))
        }

    interface IDisposable with
        member _.Dispose() = Store.Dispose()

    [<Fact>]
    member this.``A document, a compare-exchange delete and the checkpoint are committed together``() =
        task {
            let parcelId = NewId "ClaimedParcels"
            let claimKey = NewId "claims/parcels"
            do! PutClaim claimKey
            let! projection =
                this.Projection(typeof<ClusterWideParcelProjection>, [
                    Handler ProjectParcel
                    Handler (fun _ -> ReleaseClaim claimKey) ])

            do! projection.ProjectAsync(Registered parcelId, 5UL)

            let! parcel = Documents.LoadAsync<ClaimedParcel>(parcelId)
            let! claim = Claim claimKey
            let! checkpoint = Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.Equal({ Id = parcelId; Recipient = "Ada"; Events = 1 }, parcel)
            Assert.Null(claim)
            Assert.Equal(5UL, checkpoint.Value)
        }

    [<Fact>]
    member this.``When a handler fails, neither the document, the compare-exchange delete nor the checkpoint is written``() =
        task {
            let parcelId = NewId "ClaimedParcels"
            let claimKey = NewId "claims/parcels"
            do! PutClaim claimKey
            let! projection =
                this.Projection(typeof<ClusterWideParcelProjection>, [
                    Handler ProjectParcel
                    Handler (fun _ -> ReleaseClaim claimKey)
                    Handler (fun _ -> task { failwith "handler failure" }) ])

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered parcelId, 5UL) :> Task)

            let! parcel = Documents.LoadAsync<ClaimedParcel>(parcelId)
            let! claim = Claim claimKey
            let! checkpoint = Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.True(isNull (box parcel))
            Assert.Equal("Ada", claim.Value.Recipient)
            Assert.Equal(0UL, checkpoint.Value)
        }

    [<Fact>]
    member this.``A compare-exchange value changed in between makes the event run again, and it succeeds``() =
        task {
            let parcelId = NewId "ClaimedParcels"
            let claimKey = NewId "claims/parcels"
            do! PutClaim claimKey
            let attempts = ref 0
            let! projection =
                this.Projection(typeof<ClusterWideParcelProjection>, [
                    Handler ProjectParcel
                    Handler (fun _ ->
                        task {
                            attempts.Value <- attempts.Value + 1
                            let session = Session ()
                            let! claim = session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<ParcelClaim>(claimKey)
                            if attempts.Value = 1 then
                                // Another writer changes the claim after the event's session read it
                                let! changed = Store.Operations.SendAsync(PutCompareExchangeValueOperation<ParcelClaim>(claimKey, { Recipient = "Grace" }, claim.Index))
                                Assert.True(changed.Successful)
                            session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(claim)
                        }) ])

            do! projection.ProjectAsync(Registered parcelId, 5UL)

            let! parcel = Documents.LoadAsync<ClaimedParcel>(parcelId)
            let! claim = Claim claimKey
            let! checkpoint = Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.Equal(2, attempts.Value)
            Assert.Equal({ Id = parcelId; Recipient = "Ada"; Events = 1 }, parcel)
            Assert.Null(claim)
            Assert.Equal(5UL, checkpoint.Value)
        }

    [<Fact>]
    member this.``An existing checkpoint is updated, whoever wrote it``() =
        task {
            let parcelId = NewId "ClaimedParcels"
            let name = NewId "ClusterWide"
            // Written outside a cluster-wide transaction: before the projection had the attribute, or a rebuild's reset
            do! Checkpoints.StoreAsync(Checkpoint(Id = sprintf "Checkpoints-%s" name, Value = 3UL))
            let! projection = this.Projection(typeof<ClusterWideParcelProjection>, [ Handler ProjectParcel ], name)

            do! projection.ProjectAsync(Registered parcelId, 4UL)
            do! projection.ProjectAsync(Readdressed parcelId, 5UL)
            do! Checkpoints.StoreAsync(Checkpoint(Id = projection.Checkpoint.Id, Value = 0UL))
            do! projection.ProjectAsync(Readdressed parcelId, 6UL)

            let! parcel = Documents.LoadAsync<ClaimedParcel>(parcelId)
            let! checkpoint = Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.Equal({ Id = parcelId; Recipient = "Grace"; Events = 3 }, parcel)
            Assert.Equal(6UL, checkpoint.Value)
        }

    [<Fact>]
    member this.``A document stored again in its event, after a change or a delete, keeps its guard``() =
        task {
            let parcelId = NewId "ClaimedParcels"
            let! projection = this.Projection(typeof<ClusterWideParcelProjection>, [ Handler ProjectParcel ])
            do! projection.ProjectAsync(Registered parcelId, 1UL)
            let! again =
                this.Projection(typeof<ClusterWideParcelProjection>, [
                    Handler (fun _ ->
                        task {
                            let! parcel = Documents.LoadAsync<ClaimedParcel>(parcelId)
                            do! Documents.StoreAsync({ parcel with Events = 10 })
                            do! Documents.StoreAsync({ parcel with Events = 20 })
                            do! Documents.DeleteAsync<ClaimedParcel>(parcelId)
                            let! deleted = Documents.LoadAsync<ClaimedParcel>(parcelId)
                            Assert.True(isNull (box deleted))
                            do! Documents.StoreAsync({ Id = parcelId; Recipient = "Grace"; Events = 30 })
                        }) ])

            do! again.ProjectAsync(Readdressed parcelId, 2UL)

            let! parcel = Documents.LoadAsync<ClaimedParcel>(parcelId)
            Assert.Equal({ Id = parcelId; Recipient = "Grace"; Events = 30 }, parcel)
        }

    [<Fact>]
    member this.``A document stored and deleted in its event isn't written``() =
        task {
            let parcelId = NewId "ClaimedParcels"
            let! projection =
                this.Projection(typeof<ClusterWideParcelProjection>, [
                    Handler (fun _ ->
                        task {
                            do! Documents.StoreAsync({ Id = parcelId; Recipient = "Ada"; Events = 1 })
                            do! Documents.DeleteAsync<ClaimedParcel>(parcelId)
                        }) ])

            do! projection.ProjectAsync(Registered parcelId, 1UL)

            let! parcel = Documents.LoadAsync<ClaimedParcel>(parcelId)
            let! checkpoint = Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.True(isNull (box parcel))
            Assert.Equal(1UL, checkpoint.Value)
        }

    [<Fact>]
    member this.``A version bump clears the read models and replays over their atomic guards``() =
        task {
            let parcelIds = [ NewId "ClaimedParcels"; NewId "ClaimedParcels" ]
            let events = [ for id in parcelIds do yield Registered id; yield Readdressed id ]
            let name = NewId "Replayed"
            let! v1 = this.Projection(typeof<ReplayedParcelV1Projection>, [ Handler ProjectParcel ], name)
            do! Factory.RebuildIfVersioned(typeof<ReplayedParcelV1Projection>, v1)
            for ev, checkpoint in List.zip events [ 1UL .. uint64 events.Length ] do
                do! v1.ProjectAsync(ev, checkpoint)

            let! v2 = this.Projection(typeof<ReplayedParcelV2Projection>, [ Handler ProjectParcel ], name)
            do! Factory.RebuildIfVersioned(typeof<ReplayedParcelV2Projection>, v2)
            Assert.Equal(0UL, v2.Checkpoint.Value)
            let! cleared = Documents.LoadManyAsync<ClaimedParcel>(parcelIds |> List.map box |> Array.ofList)
            Assert.All(cleared.Values, fun parcel -> Assert.True(isNull (box parcel)))
            for ev, checkpoint in List.zip events [ 1UL .. uint64 events.Length ] do
                do! v2.ProjectAsync(ev, checkpoint)

            let! replayed = Documents.LoadManyAsync<ClaimedParcel>(parcelIds |> List.map box |> Array.ofList)
            let! checkpoint = Checkpoints.ReadAsync(v2.Checkpoint.Id)
            for id in parcelIds do
                Assert.Equal({ Id = id; Recipient = "Grace"; Events = 2 }, replayed.[id])
            Assert.Equal(uint64 events.Length, checkpoint.Value)
            Assert.Equal(2, checkpoint.Version)
        }

    [<Fact>]
    member this.``Only the events the attribute names are cluster-wide, and both kinds write the same document in turn``() =
        task {
            let parcelId = NewId "ClaimedParcels"
            let kinds = List<bool>()
            let! projection =
                this.Projection(typeof<RegistrationClusterWideParcelProjection>, [ Handler ProjectParcel; RecordKind kinds ])

            do! projection.ProjectAsync(Registered parcelId, 1UL)
            do! projection.ProjectAsync(Readdressed parcelId, 2UL)
            // Over the document a normal session wrote last
            do! projection.ProjectAsync(Registered parcelId, 3UL)
            do! projection.ProjectAsync(Readdressed parcelId, 4UL)

            let! parcel = Documents.LoadAsync<ClaimedParcel>(parcelId)
            let! checkpoint = Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.Equal<bool>([ true; false; true; false ], kinds)
            Assert.Equal({ Id = parcelId; Recipient = "Grace"; Events = 2 }, parcel)
            Assert.Equal(4UL, checkpoint.Value)
        }

    [<Fact>]
    member this.``A rebuild of a projection with some cluster-wide events replays over the guards its clear left``() =
        task {
            let parcelIds = [ NewId "ClaimedParcels"; NewId "ClaimedParcels" ]
            let events = [ for id in parcelIds do yield Registered id; yield Readdressed id ]
            let name = NewId "ReplayedRegistration"
            let! v1 = this.Projection(typeof<ReplayedRegistrationV1Projection>, [ Handler ProjectParcel ], name)
            do! Factory.RebuildIfVersioned(typeof<ReplayedRegistrationV1Projection>, v1)
            for ev, checkpoint in List.zip events [ 1UL .. uint64 events.Length ] do
                do! v1.ProjectAsync(ev, checkpoint)

            let! v2 = this.Projection(typeof<ReplayedRegistrationV2Projection>, [ Handler ProjectParcel ], name)
            do! Factory.RebuildIfVersioned(typeof<ReplayedRegistrationV2Projection>, v2)
            let! cleared = Documents.LoadManyAsync<ClaimedParcel>(parcelIds |> List.map box |> Array.ofList)
            Assert.All(cleared.Values, fun parcel -> Assert.True(isNull (box parcel)))
            for ev, checkpoint in List.zip events [ 1UL .. uint64 events.Length ] do
                do! v2.ProjectAsync(ev, checkpoint)

            let! replayed = Documents.LoadManyAsync<ClaimedParcel>(parcelIds |> List.map box |> Array.ofList)
            for id in parcelIds do
                Assert.Equal({ Id = id; Recipient = "Grace"; Events = 2 }, replayed.[id])
        }

    [<Fact>]
    member this.``MongoDB and PostgreSQL refuse a cluster-wide projection when it starts``() =
        task {
            use client = new MongoClient(databases.MongoReplicaSetUrl)
            let mongo = MongoDbProjectionsUnitOfWork(client, client.GetDatabase("Rosetta-ClusterWideTests")) :> IProjectionsUnitOfWork
            use database = new PostgreSqlDatabase({ ConnectionString = databases.PostgreSqlConnectionString "rosetta_cluster_wide"; Schema = "public" })
            let postgres = PostgreSqlProjectionsUnitOfWork(database) :> IProjectionsUnitOfWork

            let! mongoRefusal = Assert.ThrowsAsync<InvalidOperationException>(fun () -> mongo.PrepareAsync(typeof<ClusterWideParcelProjection>, [||]))
            let! postgresRefusal = Assert.ThrowsAsync<InvalidOperationException>(fun () -> postgres.PrepareAsync(typeof<ClusterWideParcelProjection>, [||]))

            Assert.Contains("[<ClusterWideTransaction>]", mongoRefusal.Message)
            Assert.Contains("MongoDB", mongoRefusal.Message)
            Assert.Contains("PostgreSQL", postgresRefusal.Message)
        }
