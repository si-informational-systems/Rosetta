namespace SI.Rosetta.Aggregates.MongoDB

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Driver
open SI.Rosetta.Aggregates

/// An outbox event as MongoDB holds it: a document of the outbox collection, expiring at its ExpiresAt
[<CLIMutable>]
type MongoDBOutboxEvent =
    { Id: string
      Aggregate: string
      AggregateId: string
      Version: int64
      Position: int
      /// The events union, "Namespace.Type, Assembly"; null in a document from before Rosetta 1.38
      EventsUnionClrType: string
      /// The event's record type, "Namespace.Type, Assembly"; null in a document from before Rosetta 1.38
      EventClrType: string
      EventType: string
      /// The event's record as JSON, exactly as KurrentDB stores it
      Data: string
      RecordedAt: DateTime
      /// When the TTL index deletes the document; null without a TTL, which the index leaves alone, and while it waits
      /// for its publisher
      ExpiresAt: Nullable<DateTime>
      TraceParent: string
      TraceState: string
      /// When the outbox's publisher published it; null until then
      PublishedAt: Nullable<DateTime> }

/// Records the events in the CollectionName collection, skipping the ones it already holds. With a TTL, each document
/// carries its ExpiresAt, and a TTL index on it (created once, on first use) deletes the document then. So the TTL is
/// in the documents, and changing it changes only the events recorded from then on. With a publisher, ExpiresAt is set
/// when the event is published. The publisher's lease is a document of the OutboxLeases collection
type MongoDBOutbox(db: IMongoDatabase, options: OutboxOptions) =
    let Collection = db.GetCollection<MongoDBOutboxEvent>(options.CollectionName)
    let Leases = db.GetCollection<BsonDocument>("OutboxLeases")
    let EventTTL = Option.ofNullable options.EventTTL
    let mutable Indexed = EventTTL.IsNone
    let mutable RelayIndexed = false
    let Indexing = new SemaphoreSlim(1, 1)
    let Filter = Builders<MongoDBOutboxEvent>.Filter

    /// An upsert per event that only inserts: $setOnInsert leaves a document the outbox already holds as it is
    let Upserts (events: OutboxEvent[]) : WriteModel<MongoDBOutboxEvent>[] =
        events |> Array.map (fun event ->
            let document =
                { Id = event.Id.ToString()
                  Aggregate = event.Aggregate
                  AggregateId = event.AggregateId
                  Version = event.Version
                  Position = event.Position
                  EventsUnionClrType = event.EventsUnionClrType
                  EventClrType = event.EventClrType
                  EventType = event.EventType
                  Data = event.Data
                  RecordedAt = event.RecordedAt
                  ExpiresAt =
                    match EventTTL with
                    // Waiting to be published, it doesn't expire
                    | Some ttl when not options.ExpiresFromPublication -> Nullable(event.RecordedAt.Add ttl)
                    | _ -> Nullable()
                  TraceParent = event.TraceParent
                  TraceState = event.TraceState
                  PublishedAt = Nullable() }
            let fields = document.ToBsonDocument()
            fields.Remove("_id")
            UpdateOneModel<MongoDBOutboxEvent>(
                Filter.Eq("Id", document.Id),
                BsonDocumentUpdateDefinition<MongoDBOutboxEvent>(BsonDocument("$setOnInsert", fields)),
                IsUpsert = true))

    /// The events its publisher hasn't published: PublishedAt null. Not missing, so the documents of an outbox that had
    /// no publisher before (published by the app itself) aren't published again
    let Unpublished = Filter.Type("PublishedAt", BsonType.Null)

    /// Creates the TTL index, once, before the first events are recorded: not inside a transaction, which can't create it
    member this.EnsureTTLIndexAsync() : Task =
        task {
            if not Indexed then
                do! Indexing.WaitAsync().ConfigureAwait(false)
                try
                    if not Indexed then
                        let keys = Builders<MongoDBOutboxEvent>.IndexKeys.Ascending("ExpiresAt")
                        let model = CreateIndexModel<MongoDBOutboxEvent>(keys, CreateIndexOptions(ExpireAfter = Nullable TimeSpan.Zero))
                        let! _ = Collection.Indexes.CreateOneAsync(model).ConfigureAwait(false)
                        Indexed <- true
                finally
                    Indexing.Release() |> ignore
        }

    /// The index the publisher reads the unpublished events by, created once
    member private this.EnsureRelayIndexAsync() : Task =
        task {
            if not RelayIndexed then
                let keys = Builders<MongoDBOutboxEvent>.IndexKeys.Ascending("PublishedAt").Ascending("RecordedAt").Ascending("Position")
                let! _ = Collection.Indexes.CreateOneAsync(CreateIndexModel<MongoDBOutboxEvent>(keys)).ConfigureAwait(false)
                RelayIndexed <- true
        }

    /// Inserts the events the outbox doesn't hold yet in the session's transaction, after EnsureTTLIndexAsync.
    /// Returns how many it inserted
    member this.InsertAsync(session: IClientSessionHandle, events: OutboxEvent[], cancellationToken: CancellationToken) : Task<int> =
        task {
            let! result = Collection.BulkWriteAsync(session, Upserts events, cancellationToken = cancellationToken).ConfigureAwait(false)
            return result.Upserts.Count
        }

    interface IOutbox with
        member this.RecordAsync(events: OutboxEvent[]) =
            task {
                if events.Length = 0 then return 0
                else
                    do! this.EnsureTTLIndexAsync().ConfigureAwait(false)
                    let! result = Collection.BulkWriteAsync(Upserts events).ConfigureAwait(false)
                    return result.Upserts.Count
            }

    interface IOutboxRelayStore with
        member this.TryLeaseAsync(owner, duration, cancellationToken) =
            task {
                let now = DateTime.UtcNow
                let lease = Builders<BsonDocument>.Filter
                // Taken when free, expired or already the owner's; held by another, the upsert's insert is a duplicate key
                let filter =
                    lease.And(
                        lease.Eq("_id", BsonString options.CollectionName),
                        lease.Or(lease.Eq("owner", BsonString owner), lease.Lt("expiresAt", BsonDateTime now)))
                let update = Builders<BsonDocument>.Update.Set("owner", BsonString owner).Set("expiresAt", BsonDateTime(now.Add duration))
                try
                    let! _ = Leases.UpdateOneAsync(filter, update, UpdateOptions(IsUpsert = true), cancellationToken).ConfigureAwait(false)
                    return true
                with :? MongoWriteException as ex when ex.WriteError.Category = ServerErrorCategory.DuplicateKey ->
                    return false
            }

        member this.UnpublishedAsync(max, cancellationToken) =
            task {
                do! this.EnsureRelayIndexAsync().ConfigureAwait(false)
                let sort = Builders<MongoDBOutboxEvent>.Sort.Ascending("RecordedAt").Ascending("Position")
                let! documents = Collection.Find(Unpublished).Sort(sort).Limit(Nullable max).ToListAsync(cancellationToken).ConfigureAwait(false)
                return
                    documents
                    |> Seq.map (fun document ->
                        { Id = Guid.Parse document.Id
                          Aggregate = document.Aggregate
                          AggregateId = document.AggregateId
                          Version = document.Version
                          Position = document.Position
                          EventsUnionClrType = document.EventsUnionClrType
                          EventClrType = document.EventClrType
                          EventType = document.EventType
                          Data = document.Data
                          RecordedAt = DateTime.SpecifyKind(document.RecordedAt, DateTimeKind.Utc)
                          TraceParent = document.TraceParent
                          TraceState = document.TraceState })
                    |> Seq.toArray
            }

        member this.MarkPublishedAsync(events, publishedAt, cancellationToken) =
            task {
                if events.Length > 0 then
                    let ids = events |> Array.map (fun event -> event.Id.ToString())
                    let expiresAt =
                        match EventTTL with
                        | Some ttl -> Nullable(publishedAt.Add ttl)
                        | None -> Nullable()
                    let update = Builders<MongoDBOutboxEvent>.Update.Set("PublishedAt", Nullable publishedAt).Set("ExpiresAt", expiresAt)
                    let! _ = Collection.UpdateManyAsync(Filter.In("Id", ids), update, cancellationToken = cancellationToken).ConfigureAwait(false)
                    ()
            } :> Task
