namespace SI.Rosetta.Aggregates.MongoDB

open System
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

/// Stores the AggregateState as a document in the pluralized AggregateState type name collection.
/// Version is persisted as a regular field of the document.
/// With an outbox, the aggregate's PublishedEvents are inserted with its state, in one transaction (which needs a replica
/// set), or on their own when the command changed nothing
type MongoDBAggregateRepository(db: IMongoDatabase, outbox: MongoDBOutbox option) =
    new(db: IMongoDatabase) = MongoDBAggregateRepository(db, None)

    member private this.GetCollection<'TAggregateState>() =
        db.GetCollection<'TAggregateState>(PluralizeName(typeof<'TAggregateState>.Name))

    interface IStateBasedAggregateRepository with
        member this.GetAsync<'TAggregateState
                                    when 'TAggregateState : (new : unit -> 'TAggregateState)
                                    and 'TAggregateState :> IAggregateState>
            (id: string, version: int64): System.Threading.Tasks.Task<Option<'TAggregateState>> =
            task {
                let collection = this.GetCollection<'TAggregateState>()
                let filter = Builders<'TAggregateState>.Filter.Eq("Id", id)
                let! aggregateState = collection.Find(filter).FirstOrDefaultAsync().ConfigureAwait(false)
                if isNull (box aggregateState) then return None
                else return Some aggregateState
            }

        member this.StoreAsync<'TAggregateState
                    when 'TAggregateState :> IAggregateState>
                    (aggregate: IAggregate<'TAggregateState>): System.Threading.Tasks.Task =
            task {
                let collection = this.GetCollection<'TAggregateState>()
                let filter = Builders<'TAggregateState>.Filter.Eq("Id", aggregate.Id)
                let replaceOptions = ReplaceOptions(IsUpsert = true)
                let events =
                    match outbox with
                    | Some _ -> OutboxEvents.Of aggregate
                    | None -> [||]
                if events.Length = 0 then
                    let! _ = collection.ReplaceOneAsync(filter, aggregate.GetState(), replaceOptions).ConfigureAwait(false)
                    ()
                else
                    let outbox = outbox.Value
                    do! outbox.EnsureTTLIndexAsync().ConfigureAwait(false)
                    use! session = db.Client.StartSessionAsync()
                    // Set by the transaction's last attempt, the one that commits
                    let recorded = ref 0
                    let storeWithEvents = Func<IClientSessionHandle, CancellationToken, Task<unit>>(fun session cancellationToken ->
                        task {
                            try
                                let! _ = collection.ReplaceOneAsync(session, filter, aggregate.GetState(), replaceOptions, cancellationToken).ConfigureAwait(false)
                                let! inserted = outbox.InsertAsync(session, events, cancellationToken).ConfigureAwait(false)
                                recorded.Value <- inserted
                            with ex -> ExceptionDispatchInfo.Capture(ex).Throw()
                        })
                    let! _ = session.WithTransactionAsync<unit>(storeWithEvents, TransactionOptions(writeConcern = WriteConcern.WMajority))
                    OutboxEvents.Recorded events.[0].Aggregate recorded.Value
            }

    interface IRecordPublishedEvents with
        member this.RecordPublishedEventsAsync(aggregate) =
            match outbox with
            | Some outbox -> OutboxEvents.RecordAsync outbox aggregate
            | None -> Task.CompletedTask
