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
      EventType: string
      /// The event's record as JSON, exactly as KurrentDB stores it
      Data: string
      RecordedAt: DateTime
      /// When the TTL index deletes the document; null without a TTL, which the index leaves alone
      ExpiresAt: Nullable<DateTime>
      TraceParent: string
      TraceState: string }

/// Records the events in the CollectionName collection, skipping the ones it already holds. With a TTL, each document
/// carries its ExpiresAt, and a TTL index on it (created once, on first use) deletes the document then. So the TTL is
/// in the documents, and changing it changes only the events recorded from then on
type MongoDBOutbox(db: IMongoDatabase, options: OutboxOptions) =
    let Collection = db.GetCollection<MongoDBOutboxEvent>(options.CollectionName)
    let EventTTL = Option.ofNullable options.EventTTL
    let mutable Indexed = EventTTL.IsNone
    let Indexing = new SemaphoreSlim(1, 1)

    /// An upsert per event that only inserts: $setOnInsert leaves a document the outbox already holds as it is
    let Upserts (events: OutboxEvent[]) : WriteModel<MongoDBOutboxEvent>[] =
        events |> Array.map (fun event ->
            let document =
                { Id = event.Id.ToString()
                  Aggregate = event.Aggregate
                  AggregateId = event.AggregateId
                  Version = event.Version
                  Position = event.Position
                  EventType = event.EventType
                  Data = event.Data
                  RecordedAt = event.RecordedAt
                  ExpiresAt = EventTTL |> Option.map (fun ttl -> event.RecordedAt.Add ttl) |> Option.toNullable
                  TraceParent = event.TraceParent
                  TraceState = event.TraceState }
            let fields = document.ToBsonDocument()
            fields.Remove("_id")
            UpdateOneModel<MongoDBOutboxEvent>(
                Builders<MongoDBOutboxEvent>.Filter.Eq("Id", document.Id),
                BsonDocumentUpdateDefinition<MongoDBOutboxEvent>(BsonDocument("$setOnInsert", fields)),
                IsUpsert = true))

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
