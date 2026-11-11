namespace SI.Rosetta.IntegrationTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging.Abstractions
open MongoDB.Bson
open MongoDB.Driver
open Npgsql
open Raven.Client
open Raven.Client.Documents
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Aggregates.MongoDB
open SI.Rosetta.Aggregates.PostgreSQL
open SI.Rosetta.Aggregates.RavenDB
open SI.Rosetta.Common
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Persistence.RavenDB

/// The app's publisher, as a test sees it: what it was given, in order. Throws once for the events FailOnce names
type RecordingPublisher() =
    member val Published = ConcurrentQueue<OutboxPublication>()
    member val Attempted = ConcurrentQueue<OutboxPublication>()
    member val FailOnce = ConcurrentDictionary<Guid, bool>()
    interface IOutboxPublisher with
        member this.PublishAsync(publication, _cancellationToken) =
            this.Attempted.Enqueue publication
            if this.FailOnce.TryRemove(publication.EventId) |> fst then
                Task.FromException(InvalidOperationException "The broker is down")
            else
                this.Published.Enqueue publication
                Task.CompletedTask

type Depot = { Name: string; Bays: int list }

/// Renamed from CrateLoaded: stored under its old name
[<EventType("CrateLoaded")>]
type CrateShipped = { Id: string; Seal: string option; Contents: string list; From: Depot; Weight: decimal }

type CrateUnsealed = { Id: string; By: string }

[<RequireQualifiedAccess>]
type CrateEvents =
    | Shipped of CrateShipped
    | Unsealed of CrateUnsealed
    interface IAggregateEvents

/// An aggregate as the outbox sees it: its id, its version and the events it published
type CrateAggregate(id: string, version: int64, published: IAggregateEvents list) =
    interface IAggregate with
        member _.Id = id
        member _.Version = version
        member _.Changes = List<IAggregateEvents>()
        member _.PublishedEvents = List<IAggregateEvents>(published)

/// The outbox's publisher in each store: the relay publishes what the aggregates record, in order, and marks it; the
/// lease lets one instance publish
type OutboxPublisherTests(databases: Databases) =
    let Assembly = typeof<ParcelAggregate>.Assembly
    let NewId () = sprintf "PublishedParcels-%s" (Guid.NewGuid().ToString("N"))
    let NewCollection () = sprintf "Published%s" (Guid.NewGuid().ToString("N"))

    let Readdressed id =
        let parcel = ParcelAggregate()
        parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
        parcel.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
        parcel

    let Build (configuration: IConfiguration) (register: HostApplicationBuilder -> unit) =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddConfiguration(configuration) |> ignore
        register builder
        builder.Build()

    let MongoConfiguration () =
        let url = MongoUrlBuilder(databases.MongoReplicaSetUrl.ToString(), DatabaseName = "Rosetta-OutboxPublisherTests", AuthenticationSource = "admin")
        databases.Configuration("unused", [ "MongoDB:ConnectionString", url.ToString() ])

    /// Stores the aggregate and waits for its events to be published: answers them, in the order they were published
    let PublishesWhatIsStored (host: IHost) =
        task {
            do! host.StartAsync()
            let id = NewId()
            do! host.Services.GetRequiredService<IStateBasedAggregateRepository>().StoreAsync(Readdressed id)
            let publisher = host.Services.GetRequiredService<IOutboxPublisher>() :?> RecordingPublisher
            let published () = publisher.Published |> Seq.filter (fun publication -> publication.AggregateId = id) |> Seq.toList
            do! Eventually (TimeSpan.FromSeconds 30.0) "both events are published" (fun () -> Task.FromResult(published().Length = 2))
            do! host.StopAsync()
            let publications = published ()
            Assert.Equal<(string * int) list>(
                [ "ParcelRegistered", 0; "ParcelReaddressed", 1 ],
                publications |> List.map (fun publication -> publication.EventTypeName, publication.PositionInCommand))
            Assert.Equal<ParcelEvents list>(
                [ ParcelEvents.Registered { Id = id; Recipient = "Ada" }; ParcelEvents.Readdressed { Id = id; Recipient = "Grace" } ],
                publications |> List.map (fun publication -> publication.Event :?> ParcelEvents))
            return id
        }

    /// Records a crate's events (an option, a list, a nested record, a decimal, a renamed record) with the command's
    /// trace, and a parcel's event as an older version recorded it, then publishes them with a relay over the store,
    /// whose publisher throws once: each is handed over as the aggregate published it, with what the store recorded
    let RoundTrips (outbox: IOutbox) (collection: string) =
        task {
            let id = sprintf "Crates-%s" (Guid.NewGuid().ToString("N"))
            let shipped =
                CrateEvents.Shipped
                    { Id = id; Seal = Some "S-1"; Contents = [ "Amphora"; "Scroll" ]; From = { Name = "Ostia"; Bays = [ 3; 7 ] }; Weight = 12.75m }
            let unsealed = CrateEvents.Unsealed { Id = id; By = "Customs" }
            let command = (new Activity("ShipCrate")).SetIdFormat(ActivityIdFormat.W3C).Start()
            let recorded = OutboxEvents.Of(CrateAggregate(id, 2L, [ shipped; unsealed ]))
            command.Stop()
            let parcelId = NewId()
            // ParcelRegistered, as an older version recorded it: ParcelCreatedUpcaster reads it
            let older =
                { (OutboxEvents.Of(CrateAggregate(parcelId, 1L, [ ParcelEvents.Registered { Id = parcelId; Recipient = "Ada" } ]))).[0] with
                    EventClrType = "SI.Rosetta.IntegrationTests.ParcelCreated, SI.Rosetta.IntegrationTests"
                    EventType = "ParcelCreated"
                    Data = sprintf """{"Id":"%s","Addressee":"Ada"}""" parcelId
                    RecordedAt = recorded.[0].RecordedAt.AddSeconds 1.0 }
            let! _ = outbox.RecordAsync(Array.append recorded [| older |])
            let publisher = RecordingPublisher()
            publisher.FailOnce.[recorded.[0].Id] <- true
            use relay =
                new OutboxRelay(outbox, publisher, [ ParcelCreatedUpcaster() :> IEventUpcaster ],
                                OutboxOptions(CollectionName = collection, PublishPollInterval = TimeSpan.FromMilliseconds 100.0), NullLogger<OutboxRelay>.Instance)

            do! relay.StartAsync(CancellationToken.None)
            do! Eventually (TimeSpan.FromSeconds 30.0) "the events are published" (fun () -> Task.FromResult(publisher.Published.Count = 3))
            do! relay.StopAsync(CancellationToken.None)

            Assert.Equal<int list>([ 1; 2; 1; 1 ], publisher.Attempted |> Seq.map (fun publication -> publication.Attempt) |> List.ofSeq)
            let published = publisher.Published |> List.ofSeq
            Assert.Equal<IAggregateEvents list>(
                [ shipped :> IAggregateEvents; unsealed; ParcelEvents.Registered { Id = parcelId; Recipient = "Ada" } ],
                published |> List.map (fun publication -> publication.Event))
            Assert.Equal<obj list>(
                [ (match shipped with CrateEvents.Shipped e -> box e | _ -> null)
                  (match unsealed with CrateEvents.Unsealed e -> box e | _ -> null)
                  box ({ Id = parcelId; Recipient = "Ada" } : ParcelRegistered) ],
                published |> List.map (fun publication -> publication.EventRecord))
            Assert.Equal<Type list>(
                [ typeof<CrateShipped>; typeof<CrateUnsealed>; typeof<ParcelRegistered> ],
                published |> List.map (fun publication -> publication.EventRecordType))
            Assert.Equal<Type list>(
                [ typeof<CrateEvents>; typeof<CrateEvents>; typeof<ParcelEvents> ],
                published |> List.map (fun publication -> publication.EventsUnionType))
            Assert.Equal<string list>([ "CrateLoaded"; "CrateUnsealed"; "ParcelCreated" ], published |> List.map (fun publication -> publication.EventTypeName))
            for publication, event in List.zip published (List.ofArray (Array.append recorded [| older |])) do
                Assert.Equal(event.Id, publication.EventId)
                Assert.Equal(event.Data, publication.EventJson)
                Assert.Equal("Crate", publication.AggregateName)
                Assert.Equal(event.AggregateId, publication.AggregateId)
                Assert.Equal(event.Version, publication.AggregateVersion)
                Assert.Equal(event.Position, publication.PositionInCommand)
                // MongoDB keeps milliseconds
                Assert.Equal(event.RecordedAt, publication.RecordedAt, TimeSpan.FromMilliseconds 1.0)
                Assert.Equal(DateTimeKind.Utc, publication.RecordedAt.Kind)
            for publication in published.[0..1] do
                Assert.Equal(command.TraceId, publication.CommandTrace.TraceId)
                Assert.Equal(command.SpanId, publication.CommandTrace.SpanId)
            // Recorded without a trace
            Assert.Equal(ActivityContext(), published.[2].CommandTrace)
            let! unpublished = (outbox :?> IOutboxRelayStore).UnpublishedAsync(10, CancellationToken.None)
            Assert.Empty(unpublished)
        } :> Task

    /// An event a store recorded with Rosetta 1.37 is read without its types, and can't be decoded
    let ReadsOlderRecord (store: IOutboxRelayStore) (aggregateId: string) =
        task {
            let! unpublished = store.UnpublishedAsync(10, CancellationToken.None)
            let older = unpublished |> Array.find (fun event -> event.AggregateId = aggregateId)
            Assert.Null(older.EventsUnionClrType)
            Assert.Null(older.EventClrType)
            Assert.Equal("ParcelRegistered", older.EventType)
            Assert.Equal(Error OutboxDecodeError.NoEventsUnion, OutboxEvents.Decode Seq.empty older)
        } :> Task

    /// Two owners: the first takes the lease, the second can't while it holds it, the first renews it, and once it
    /// expires the second takes it
    let OneOwnerAtATime (store: IOutboxRelayStore) =
        task {
            let short = TimeSpan.FromSeconds 2.0
            let! first = store.TryLeaseAsync("instance-1", short, CancellationToken.None)
            let! second = store.TryLeaseAsync("instance-2", TimeSpan.FromMinutes 1.0, CancellationToken.None)
            let! renewed = store.TryLeaseAsync("instance-1", short, CancellationToken.None)
            Assert.True(first)
            Assert.False(second)
            Assert.True(renewed)
            do! Task.Delay(TimeSpan.FromSeconds 3.0)
            let! takenOver = store.TryLeaseAsync("instance-2", TimeSpan.FromMinutes 1.0, CancellationToken.None)
            Assert.True(takenOver)
            // Handed over when it stops: a lease for no time
            let! handedOver = store.TryLeaseAsync("instance-2", TimeSpan.Zero, CancellationToken.None)
            do! Task.Delay 20
            let! takenAtOnce = store.TryLeaseAsync("instance-3", TimeSpan.FromMinutes 1.0, CancellationToken.None)
            Assert.True(handedOver)
            Assert.True(takenAtOnce)
        } :> Task

    let Execute (database: PostgreSqlDatabase) (sql: string) =
        task {
            use! connection = database.DataSource.OpenConnectionAsync()
            use command = new NpgsqlCommand(sql, connection)
            let! _ = command.ExecuteNonQueryAsync()
            ()
        }

    [<Fact>]
    member this.``RavenDB: the relay publishes what's stored, then the events expire from their publication``() =
        task {
            let collection = NewCollection()
            use host = Build (databases.Configuration "Rosetta-OutboxPublisherTests") (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<RavenDB>(Assembly)
                    .UseOutboxWith<RavenDB>(fun outbox ->
                        outbox.CollectionName <- collection
                        outbox.EventTTL <- Nullable(TimeSpan.FromHours 1.0)
                        outbox.PublishPollInterval <- TimeSpan.FromMilliseconds 100.0)
                    .UseOutboxPublisher<RecordingPublisher>() |> ignore)

            let! id = PublishesWhatIsStored host

            use session = host.Services.GetRequiredService<IDocumentStore>().OpenAsyncSession()
            let! documents = session.Advanced.LoadStartingWithAsync<RavenDBOutboxEvent>(collection + "/", pageSize = 1024)
            let documents = documents |> Seq.filter (fun document -> document.AggregateId = id) |> Seq.toList
            Assert.Equal(2, documents.Length)
            for document in documents do
                let publishedAt = document.PublishedAt.Value
                let metadata = session.Advanced.GetMetadataFor(document)
                let expires = DateTime.Parse(metadata.GetString Constants.Documents.Metadata.Expires, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal)
                Assert.Equal(publishedAt.AddHours 1.0, expires, TimeSpan.FromSeconds 1.0)
                Assert.Equal(collection, metadata.GetString Constants.Documents.Metadata.Collection)
        }

    [<Fact>]
    member this.``RavenDB: the publisher is handed each event decoded, with what was recorded``() =
        task {
            use store = RavenDocumentStoreFactory.CreateAndInitializeDocumentStore (RavenConfig.FromConfiguration(databases.Configuration "Rosetta-OutboxPublisherTests")) None
            let collection = NewCollection()
            do! RoundTrips (RavenDBOutbox(store, OutboxOptions(CollectionName = collection))) collection

            // Recorded by Rosetta 1.37
            let older = NewCollection()
            use session = store.OpenAsyncSession()
            let aggregateId = NewId()
            let document = {| Id = sprintf "%s/%O" older (Guid.NewGuid()); Aggregate = "Parcel"; AggregateId = aggregateId; Version = 1L; Position = 0
                              EventType = "ParcelRegistered"; Data = "{}"; RecordedAt = DateTime.UtcNow; TraceParent = null; TraceState = null
                              PublishedAt = Nullable<DateTime>() |}
            do! session.StoreAsync(document, document.Id)
            session.Advanced.GetMetadataFor(document).[Constants.Documents.Metadata.Collection] <- box older
            do! session.SaveChangesAsync()
            do! ReadsOlderRecord (RavenDBOutbox(store, OutboxOptions(CollectionName = older))) aggregateId
        }

    [<Fact>]
    member this.``RavenDB: one instance holds the lease, and an event recorded without PublishedAt isn't published``() =
        task {
            use store = RavenDocumentStoreFactory.CreateAndInitializeDocumentStore (RavenConfig.FromConfiguration(databases.Configuration "Rosetta-OutboxPublisherTests")) None
            let collection = NewCollection()
            let outbox = RavenDBOutbox(store, OutboxOptions(CollectionName = collection))

            do! OneOwnerAtATime outbox

            // Recorded by an outbox that had no publisher: the app published it itself
            use session = store.OpenAsyncSession()
            let older = {| Id = sprintf "%s/%O" collection (Guid.NewGuid()); Aggregate = "Parcel"; AggregateId = NewId(); Version = 1L; Position = 0
                           EventType = "ParcelRegistered"; Data = "{}"; RecordedAt = DateTime.UtcNow; TraceParent = null; TraceState = null |}
            do! session.StoreAsync(older, older.Id)
            session.Advanced.GetMetadataFor(older).[Constants.Documents.Metadata.Collection] <- box collection
            do! session.SaveChangesAsync()
            do! (outbox :> IOutbox).RecordAsync(OutboxEvents.Of(Readdressed (NewId()))) :> Task

            let! unpublished = (outbox :> IOutboxRelayStore).UnpublishedAsync(10, CancellationToken.None)
            Assert.Equal(2, unpublished.Length)
            Assert.DoesNotContain(older.AggregateId, unpublished |> Array.map (fun event -> event.AggregateId))
        }

    [<Fact>]
    member this.``MongoDB: the relay publishes what's stored, then the events expire from their publication``() =
        task {
            let collection = NewCollection()
            use host = Build (MongoConfiguration ()) (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<MongoDB>(Assembly)
                    .UseOutboxWith<MongoDB>(fun outbox ->
                        outbox.CollectionName <- collection
                        outbox.EventTTL <- Nullable(TimeSpan.FromHours 1.0)
                        outbox.PublishPollInterval <- TimeSpan.FromMilliseconds 100.0)
                    .UseOutboxPublisher<RecordingPublisher>() |> ignore)

            let! id = PublishesWhatIsStored host

            let documents = host.Services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>(collection)
            let! recorded = documents.Find(Builders<BsonDocument>.Filter.Eq("aggregateId", id)).ToListAsync()
            Assert.Equal(2, recorded.Count)
            for document in recorded do
                let publishedAt = document.["publishedAt"].ToUniversalTime()
                Assert.Equal(publishedAt.AddHours 1.0, document.["expiresAt"].ToUniversalTime(), TimeSpan.FromSeconds 1.0)
        }

    [<Fact>]
    member this.``MongoDB: the publisher is handed each event decoded, with what was recorded``() =
        task {
            use host = Build (MongoConfiguration ()) (fun builder -> builder.UseOutboxWith<MongoDB>() |> ignore)
            let db = host.Services.GetRequiredService<IMongoDatabase>()
            let collection = NewCollection()
            do! RoundTrips (MongoDBOutbox(db, OutboxOptions(CollectionName = collection))) collection

            // Recorded by Rosetta 1.37
            let older = NewCollection()
            let aggregateId = NewId()
            do! db.GetCollection<BsonDocument>(older).InsertOneAsync(
                    BsonDocument(
                        [ BsonElement("_id", BsonString(Guid.NewGuid().ToString())); BsonElement("aggregate", BsonString "Parcel")
                          BsonElement("aggregateId", BsonString aggregateId); BsonElement("version", BsonInt64 1L); BsonElement("position", BsonInt32 0)
                          BsonElement("eventType", BsonString "ParcelRegistered"); BsonElement("data", BsonString "{}")
                          BsonElement("recordedAt", BsonDateTime DateTime.UtcNow); BsonElement("expiresAt", BsonNull.Value)
                          BsonElement("traceParent", BsonNull.Value); BsonElement("traceState", BsonNull.Value); BsonElement("publishedAt", BsonNull.Value) ]))
            do! ReadsOlderRecord (MongoDBOutbox(db, OutboxOptions(CollectionName = older))) aggregateId
        }

    [<Fact>]
    member this.``MongoDB: one instance holds the lease, and an event recorded without PublishedAt isn't published``() =
        task {
            use host = Build (MongoConfiguration ()) (fun builder -> builder.UseOutboxWith<MongoDB>() |> ignore)
            let db = host.Services.GetRequiredService<IMongoDatabase>()
            let collection = NewCollection()
            let outbox = MongoDBOutbox(db, OutboxOptions(CollectionName = collection))

            do! OneOwnerAtATime outbox

            let older = NewId()
            do! db.GetCollection<BsonDocument>(collection).InsertOneAsync(
                    BsonDocument([ BsonElement("_id", BsonString(Guid.NewGuid().ToString())); BsonElement("aggregateId", BsonString older)
                                   BsonElement("recordedAt", BsonDateTime DateTime.UtcNow); BsonElement("position", BsonInt32 0) ]))
            do! (outbox :> IOutbox).RecordAsync(OutboxEvents.Of(Readdressed (NewId()))) :> Task

            let! unpublished = (outbox :> IOutboxRelayStore).UnpublishedAsync(10, CancellationToken.None)
            Assert.Equal(2, unpublished.Length)
            Assert.DoesNotContain(older, unpublished |> Array.map (fun event -> event.AggregateId))
        }

    [<Fact>]
    member this.``PostgreSQL: the relay publishes what's stored, then the events expire from their publication``() =
        task {
            let databaseName = sprintf "rosetta_publisher_%s" (Guid.NewGuid().ToString("N"))
            let connectionString = databases.PostgreSqlConnectionString databaseName
            use database = new PostgreSqlDatabase({ ConnectionString = connectionString; Schema = "public" })
            do! database.EnsureCreatedAsync()
            do! Execute database (File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schema", "aggregates.sql")))
            use host = Build (databases.Configuration("unused", [ "PostgreSQL:ConnectionString", connectionString ])) (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<PostgreSQL>(Assembly)
                    .UseOutboxWith<PostgreSQL>(fun outbox ->
                        outbox.EventTTL <- Nullable(TimeSpan.FromHours 1.0)
                        outbox.PublishPollInterval <- TimeSpan.FromMilliseconds 100.0)
                    .UseOutboxPublisher<RecordingPublisher>() |> ignore)

            let! id = PublishesWhatIsStored host

            use! connection = database.DataSource.OpenConnectionAsync()
            use select = new NpgsqlCommand("select published_at, expires_at from rosetta.outbox where aggregate_id = $1", connection)
            select.Parameters.Add(NpgsqlParameter(Value = id)) |> ignore
            use! reader = select.ExecuteReaderAsync()
            let mutable rows = 0
            while reader.Read() do
                rows <- rows + 1
                Assert.Equal(reader.GetDateTime(0).AddHours 1.0, reader.GetDateTime 1, TimeSpan.FromSeconds 1.0)
            Assert.Equal(2, rows)
            do! OneOwnerAtATime (PostgreSqlOutbox(database, OutboxOptions(CollectionName = "LeasedOutbox")))
        }

    [<Fact>]
    member this.``PostgreSQL: the publisher is handed each event decoded, with what was recorded, and a 1.37 table gains the types``() =
        task {
            let databaseName = sprintf "rosetta_publications_%s" (Guid.NewGuid().ToString("N"))
            use database = new PostgreSqlDatabase({ ConnectionString = databases.PostgreSqlConnectionString databaseName; Schema = "public" })
            do! database.EnsureCreatedAsync()
            do! RoundTrips (PostgreSqlOutbox(database, OutboxOptions(CollectionName = "CrateOutbox"))) "CrateOutbox"

            // The table and an event as Rosetta 1.37 created and recorded them
            let aggregateId = NewId()
            do! Execute database (
                    String.concat "\n" [
                        "create schema if not exists rosetta;"
                        "create table rosetta.older_outbox (id uuid primary key, aggregate text not null, aggregate_id text not null, version bigint not null,"
                        "    position int not null, event_type text not null, data json not null, recorded_at timestamptz not null, expires_at timestamptz,"
                        "    trace_parent text, trace_state text, published_at timestamptz);"
                        sprintf "insert into rosetta.older_outbox (id, aggregate, aggregate_id, version, position, event_type, data, recorded_at) values ('%O', 'Parcel', '%s', 1, 0, 'ParcelRegistered', '{}', now());" (Guid.NewGuid()) aggregateId ])
            let outbox = PostgreSqlOutbox(database, OutboxOptions(CollectionName = "OlderOutbox"))
            do! ReadsOlderRecord outbox aggregateId
            // And it records the types from then on
            let! _ = (outbox :> IOutbox).RecordAsync(OutboxEvents.Of(Readdressed (NewId())))
            let! unpublished = (outbox :> IOutboxRelayStore).UnpublishedAsync(10, CancellationToken.None)
            Assert.Equal(2, unpublished |> Array.filter (fun event -> event.EventsUnionClrType = "SI.Rosetta.IntegrationTests.ParcelEvents, SI.Rosetta.IntegrationTests") |> Array.length)
        }
