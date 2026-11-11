namespace SI.Rosetta.IntegrationTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
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
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Persistence.RavenDB

/// The app's publisher, as a test sees it: what it was given, in order
type RecordingPublisher() =
    member val Published = ConcurrentQueue<OutboxEvent>()
    interface IOutboxPublisher with
        member this.PublishAsync(event, _cancellationToken) =
            this.Published.Enqueue event
            Task.CompletedTask

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
            let published () = publisher.Published |> Seq.filter (fun event -> event.AggregateId = id) |> Seq.toList
            do! Eventually (TimeSpan.FromSeconds 30.0) "both events are published" (fun () -> Task.FromResult(published().Length = 2))
            do! host.StopAsync()
            let events = published ()
            Assert.Equal<(string * int) list>([ "ParcelRegistered", 0; "ParcelReaddressed", 1 ], events |> List.map (fun event -> event.EventType, event.Position))
            return id
        }

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
