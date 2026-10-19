namespace SI.Rosetta.IntegrationTests

open System
open System.Globalization
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open MongoDB.Bson
open MongoDB.Driver
open Raven.Client
open Raven.Client.Documents
open Raven.Client.Documents.Operations.Expiration
open Raven.Client.ServerWide.Operations
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Aggregates.KurrentDB
open SI.Rosetta.Aggregates.RavenDB
open SI.Rosetta.Common
open SI.Rosetta.Persistence.RavenDB

/// The outbox in RavenDB and MongoDB, as UseOutboxWith registers it
type OutboxTests(databases: Databases) =
    let RavenDatabase = "Rosetta-OutboxTests"
    let MongoDatabase = "Rosetta-OutboxTests"
    let Assembly = typeof<ParcelAggregate>.Assembly

    // Not the Parcels category, which HostingTests projects
    let NewId () = sprintf "OutboxParcels-%s" (Guid.NewGuid().ToString("N"))
    let NewDatabase () = sprintf "Rosetta-OutboxTests-%s" (Guid.NewGuid().ToString("N"))

    /// A recorded event, read back as whatever reads the outbox would
    let Decode (eventType: string) (data: string) = EventEncoding.Decode<ParcelEvents> Seq.empty eventType data

    let Registered id =
        let parcel = ParcelAggregate()
        parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
        parcel

    let Readdressed id =
        let parcel = Registered id
        parcel.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
        parcel

    let Build (configuration: IConfiguration) (register: HostApplicationBuilder -> unit) =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddConfiguration(configuration) |> ignore
        register builder
        builder.Build()

    let RavenHost (database: string) register = Build (databases.Configuration database) register

    let MongoHost register =
        let url = MongoUrlBuilder(databases.MongoReplicaSetUrl.ToString(), DatabaseName = MongoDatabase, AuthenticationSource = "admin")
        Build (databases.Configuration("unused", [ "MongoDB:ConnectionString", url.ToString() ])) register

    /// The aggregate's outbox documents in RavenDB, in the order they were recorded
    let RavenOutboxOf (store: IDocumentStore) (collection: string) (aggregateId: string) =
        task {
            use session = store.OpenAsyncSession()
            let! documents = session.Advanced.LoadStartingWithAsync<RavenDBOutboxEvent>(collection + "/", pageSize = 1024)
            return
                documents
                |> Seq.filter (fun document -> document.AggregateId = aggregateId)
                |> Seq.sortBy (fun document -> document.RecordedAt, document.Position)
                |> Seq.map (fun document ->
                    let metadata = session.Advanced.GetMetadataFor(document)
                    let expires =
                        if metadata.ContainsKey Constants.Documents.Metadata.Expires then
                            Some (DateTime.Parse(metadata.GetString Constants.Documents.Metadata.Expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal))
                        else None
                    document, metadata.GetString Constants.Documents.Metadata.Collection, expires)
                |> Seq.toList
        }

    /// The change vector of an outbox document in RavenDB: a new one each time it's saved
    let RavenChangeVectorOf (store: IDocumentStore) (event: RavenDBOutboxEvent) =
        task {
            use session = store.OpenAsyncSession()
            let! document = session.LoadAsync<RavenDBOutboxEvent>(event.Id)
            return session.Advanced.GetChangeVectorFor(document)
        }

    /// The aggregate's outbox documents in MongoDB, in the order they were recorded
    let MongoOutboxOf (db: IMongoDatabase) (collection: string) (aggregateId: string) =
        task {
            let documents = db.GetCollection<BsonDocument>(collection)
            let! recorded =
                documents.Find(Builders<BsonDocument>.Filter.Eq("aggregateId", aggregateId))
                    .Sort(Builders<BsonDocument>.Sort.Ascending("recordedAt").Ascending("position"))
                    .ToListAsync()
            return List.ofSeq recorded
        }

    [<Fact>]
    member this.``RavenDB records a state-based aggregate's published events with its state``() =
        task {
            use host = RavenHost RavenDatabase (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<RavenDB>(Assembly)
                    .UseOutboxWith<RavenDB>(fun outbox ->
                        outbox.CollectionName <- "ParcelEvents"
                        outbox.EventTTL <- Nullable(TimeSpan.FromHours 1.0)) |> ignore)
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()

            do! repository.StoreAsync(Readdressed id)

            let! recorded = RavenOutboxOf (host.Services.GetRequiredService<IDocumentStore>()) "ParcelEvents" id
            Assert.Equal<string list>([ "ParcelRegistered"; "ParcelReaddressed" ], recorded |> List.map (fun (document, _, _) -> document.EventType))
            Assert.Equal<int list>([ 0; 1 ], recorded |> List.map (fun (document, _, _) -> document.Position))
            for document, collection, expires in recorded do
                Assert.Equal("Parcel", document.Aggregate)
                Assert.Equal(2L, document.Version)
                Assert.Equal("ParcelEvents", collection)
                Assert.Equal(document.RecordedAt.AddHours 1.0, expires.Value, TimeSpan.FromSeconds 1.0)
            let readdressed, _, _ = recorded.[1]
            Assert.Equal(Ok (ParcelEvents.Readdressed { Id = id; Recipient = "Grace" }), Decode readdressed.EventType readdressed.Data)
        }

    [<Fact>]
    member this.``RavenDB stores neither the state nor its events when recording the events fails``() =
        task {
            use store = RavenDocumentStoreFactory.CreateAndInitializeDocumentStore (RavenConfig.FromConfiguration(databases.Configuration RavenDatabase)) None
            store.OnBeforeStore.Add(fun stored ->
                if stored.Entity :? RavenDBOutboxEvent then raise (InvalidOperationException "The outbox is unavailable"))
            let repository = RavenDBAggregateRepository(store, Some (RavenDBOutbox(store, OutboxOptions()))) :> IAggregateRepository
            let id = NewId()

            let! _ = Assert.ThrowsAsync<InvalidOperationException>(fun () -> repository.StoreAsync(Registered id))

            let! stored = repository.GetAsync<ParcelAggregateState>(id)
            Assert.True(stored.IsNone)
        }

    [<Fact>]
    member this.``RavenDB turns the database's document expiration on for a TTL``() =
        task {
            let database = NewDatabase()
            use host = RavenHost database (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<RavenDB>(Assembly)
                    .UseOutboxWith<RavenDB>(fun outbox -> outbox.EventTTL <- Nullable(TimeSpan.FromDays 1.0)) |> ignore)
            let store = host.Services.GetRequiredService<IDocumentStore>()
            let! before = store.Maintenance.Server.SendAsync(GetDatabaseRecordOperation(database))
            Assert.True(isNull before.Expiration || before.Expiration.Disabled)

            do! host.Services.GetRequiredService<IStateBasedAggregateRepository>().StoreAsync(Registered (NewId()))

            let! after = store.Maintenance.Server.SendAsync(GetDatabaseRecordOperation(database))
            Assert.False(after.Expiration.Disabled)
        }

    [<Fact>]
    member this.``RavenDB deletes the events once their TTL passes``() =
        task {
            let database = NewDatabase()
            use host = RavenHost database (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<RavenDB>(Assembly)
                    .UseOutboxWith<RavenDB>(fun outbox -> outbox.EventTTL <- Nullable(TimeSpan.FromSeconds 3.0)) |> ignore)
            let store = host.Services.GetRequiredService<IDocumentStore>()
            // Expiration already on: the outbox leaves its settings alone
            let! _ = store.Maintenance.SendAsync(ConfigureExpirationOperation(ExpirationConfiguration(Disabled = false, DeleteFrequencyInSec = Nullable 1L)))
            let id = NewId()

            do! host.Services.GetRequiredService<IStateBasedAggregateRepository>().StoreAsync(Registered id)

            let! recorded = RavenOutboxOf store "Outbox" id
            Assert.Single(recorded) |> ignore
            do! Eventually (TimeSpan.FromSeconds 60.0) "RavenDB deletes the expired event" (fun () ->
                    task {
                        let! recorded = RavenOutboxOf store "Outbox" id
                        return recorded.IsEmpty
                    })
            let! record = store.Maintenance.Server.SendAsync(GetDatabaseRecordOperation(database))
            Assert.Equal(Nullable 1L, record.Expiration.DeleteFrequencyInSec)
        }

    [<Fact>]
    member this.``An event-sourced aggregate's published events are recorded once its events are appended``() =
        task {
            use host = RavenHost RavenDatabase (fun builder ->
                builder.UseAggregatesWithEventSourcing<KurrentDB>(Assembly)
                    .UseOutboxWith<RavenDB>(fun outbox -> outbox.CollectionName <- "ParcelEventsFromKurrentDB") |> ignore)
            let handler = host.Services.GetRequiredService<IParcelAggregateHandler>()
            let id = NewId()

            do! handler.ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            do! handler.ExecuteAsync(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            // Idempotent: nothing is appended, and the event it publishes again is already in the outbox
            do! handler.ExecuteAsync(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })

            let! recorded = RavenOutboxOf (host.Services.GetRequiredService<IDocumentStore>()) "ParcelEventsFromKurrentDB" id
            Assert.Equal<(string * int64) list>(
                [ "ParcelRegistered", 1L; "ParcelReaddressed", 2L ],
                recorded |> List.map (fun (document, _, _) -> document.EventType, document.Version))
            Assert.All(recorded, fun (_, _, expires) -> Assert.True(expires.IsNone))
        }

    [<Fact>]
    member this.``A redelivered command records the events a crash kept out of the outbox``() =
        task {
            let id = NewId()
            // The first attempt appended its events to KurrentDB, and crashed before recording them
            do! (KurrentDBAggregateRepository(databases.KurrentDBClient) :> IAggregateRepository).StoreAsync(Registered id)
            use host = RavenHost RavenDatabase (fun builder ->
                builder.UseAggregatesWithEventSourcing<KurrentDB>(Assembly)
                    .UseOutboxWith<RavenDB>(fun outbox -> outbox.CollectionName <- "RedeliveredParcelEvents") |> ignore)

            do! host.Services.GetRequiredService<IParcelAggregateHandler>().ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })

            let! recorded = RavenOutboxOf (host.Services.GetRequiredService<IDocumentStore>()) "RedeliveredParcelEvents" id
            let registered, _, _ = Assert.Single(recorded)
            Assert.Equal(1L, registered.Version)
            Assert.Equal(Ok (ParcelEvents.Registered { Id = id; Recipient = "Ada" }), Decode registered.EventType registered.Data)
        }

    [<Fact>]
    member this.``RavenDB leaves an event it holds as it is when an unchanged aggregate publishes it again``() =
        task {
            use host = RavenHost RavenDatabase (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<RavenDB>(Assembly)
                    .UseOutboxWith<RavenDB>(fun outbox -> outbox.CollectionName <- "RepeatedParcelEvents") |> ignore)
            let store = host.Services.GetRequiredService<IDocumentStore>()
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()
            do! repository.StoreAsync(Registered id)
            let! first = RavenOutboxOf store "RepeatedParcelEvents" id
            let firstEvent, _, _ = Assert.Single(first)
            let! firstChangeVector = RavenChangeVectorOf store firstEvent
            let! stored = repository.GetAsync<ParcelAggregateState>(id)
            let repeated = ParcelAggregate(State = stored.Value)
            repeated.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })

            do! (repository :?> IRecordPublishedEvents).RecordPublishedEventsAsync(repeated)

            let! recorded = RavenOutboxOf store "RepeatedParcelEvents" id
            let event, _, _ = Assert.Single(recorded)
            let! changeVector = RavenChangeVectorOf store event
            Assert.Equal(firstEvent.RecordedAt, event.RecordedAt)
            Assert.Equal(firstChangeVector, changeVector)
        }

    [<Fact>]
    member this.``MongoDB records a state-based aggregate's published events with its state, in a transaction``() =
        task {
            use host = MongoHost (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<MongoDB>(Assembly)
                    .UseOutboxWith<MongoDB>(fun outbox -> outbox.EventTTL <- Nullable(TimeSpan.FromHours 1.0)) |> ignore)
            let db = host.Services.GetRequiredService<IMongoDatabase>()
            let id = NewId()

            do! host.Services.GetRequiredService<IStateBasedAggregateRepository>().StoreAsync(Readdressed id)

            let! recorded = MongoOutboxOf db "Outbox" id
            Assert.Equal<string list>([ "ParcelRegistered"; "ParcelReaddressed" ], recorded |> List.map (fun document -> document.["eventType"].AsString))
            for document in recorded do
                Assert.Equal("Parcel", document.["aggregate"].AsString)
                Assert.Equal(2L, document.["version"].ToInt64())
                Assert.Equal(document.["recordedAt"].ToUniversalTime().AddHours 1.0, document.["expiresAt"].ToUniversalTime(), TimeSpan.FromSeconds 1.0)
            Assert.Equal(Ok (ParcelEvents.Readdressed { Id = id; Recipient = "Grace" }), Decode recorded.[1].["eventType"].AsString recorded.[1].["data"].AsString)
            let! indexes = db.GetCollection<BsonDocument>("Outbox").Indexes.ListAsync()
            let! indexes = indexes.ToListAsync()
            let ttl = indexes |> Seq.find (fun index -> index.["key"].AsBsonDocument.Contains "expiresAt")
            Assert.Equal(0, ttl.["expireAfterSeconds"].ToInt32())
        }

    [<Fact>]
    member this.``MongoDB leaves an event it holds as it is when an unchanged aggregate publishes it again``() =
        task {
            use host = MongoHost (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<MongoDB>(Assembly)
                    .UseOutboxWith<MongoDB>(fun outbox -> outbox.CollectionName <- "RepeatedOutbox") |> ignore)
            let db = host.Services.GetRequiredService<IMongoDatabase>()
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()
            do! repository.StoreAsync(Registered id)
            let! first = MongoOutboxOf db "RepeatedOutbox" id
            let firstEvent = Assert.Single(first)
            let! stored = repository.GetAsync<ParcelAggregateState>(id)
            let repeated = ParcelAggregate(State = stored.Value)
            repeated.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })

            do! (repository :?> IRecordPublishedEvents).RecordPublishedEventsAsync(repeated)

            let! recorded = MongoOutboxOf db "RepeatedOutbox" id
            Assert.Equal<BsonDocument>(firstEvent, Assert.Single(recorded))
        }

    [<Fact>]
    member this.``MongoDB stores neither the state nor its events when recording the events fails``() =
        task {
            use host = MongoHost (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<MongoDB>(Assembly)
                    .UseOutboxWith<MongoDB>(fun outbox -> outbox.CollectionName <- "RefusingOutbox") |> ignore)
            let db = host.Services.GetRequiredService<IMongoDatabase>()
            // An aggregate's second event breaks it
            let! _ = db.GetCollection<BsonDocument>("RefusingOutbox").Indexes.CreateOneAsync(
                        CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("aggregateId"), CreateIndexOptions(Unique = true)))
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()

            let! _ = Assert.ThrowsAnyAsync<MongoException>(fun () -> repository.StoreAsync(Readdressed id))

            let! stored = repository.GetAsync<ParcelAggregateState>(id)
            let! recorded = MongoOutboxOf db "RefusingOutbox" id
            Assert.True(stored.IsNone)
            Assert.Empty(recorded)
        }

    [<Fact>]
    member this.``MongoDB deletes the events once their TTL passes``() =
        task {
            use host = MongoHost (fun builder ->
                builder.UseAggregatesWithEventSourcing<KurrentDB>(Assembly)
                    .UseOutboxWith<MongoDB>(fun outbox ->
                        outbox.CollectionName <- "ExpiringOutbox"
                        outbox.EventTTL <- Nullable(TimeSpan.FromSeconds 3.0)) |> ignore)
            // The TTL monitor deletes the expired documents every 60 s, unless told otherwise
            let setParameter = BsonDocument([ BsonElement("setParameter", BsonInt32 1); BsonElement("ttlMonitorSleepSecs", BsonInt32 1) ])
            let! _ = host.Services.GetRequiredService<IMongoClient>().GetDatabase("admin").RunCommandAsync<BsonDocument>(BsonDocumentCommand<BsonDocument>(setParameter))
            let db = host.Services.GetRequiredService<IMongoDatabase>()
            let id = NewId()

            let handler = host.Services.GetRequiredService<IParcelAggregateHandler>()
            do! handler.ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            // Idempotent, and its event is already in the outbox
            do! handler.ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })

            let! recorded = MongoOutboxOf db "ExpiringOutbox" id
            Assert.Single(recorded) |> ignore
            do! Eventually (TimeSpan.FromSeconds 120.0) "MongoDB's TTL monitor deletes the expired event" (fun () ->
                    task {
                        let! recorded = MongoOutboxOf db "ExpiringOutbox" id
                        return recorded.IsEmpty
                    })
        }
