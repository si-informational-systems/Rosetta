namespace SI.Rosetta.Aggregates.MongoDB

open System
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

/// Stores the AggregateState as a document in the pluralized AggregateState type name collection.
/// Version is persisted as a regular field of the document, and every write is checked against the version the aggregate
/// was loaded at (ConcurrencyException when another command got there first), so a command never overwrites another's.
/// With an outbox, the aggregate's PublishedEvents are inserted with its state, in one transaction (which needs a replica
/// set), or on their own when the command changed nothing
type MongoDBAggregateRepository(db: IMongoDatabase, outbox: MongoDBOutbox option) =
    new(db: IMongoDatabase) = MongoDBAggregateRepository(db, None)

    member private this.GetCollection<'TAggregateState>() =
        db.GetCollection<'TAggregateState>(PluralizeName(typeof<'TAggregateState>.Name))

    /// Writes the state unless another command got there first: a new aggregate is inserted (its Id taken meanwhile:
    /// a duplicate key), a loaded one replaced only while its stored Version is still the one it was loaded at.
    /// Otherwise ConcurrencyException. In the session's transaction when there's one (session not null)
    static member private WriteAsync<'TAggregateState>
        (collection: IMongoCollection<'TAggregateState>, session: IClientSessionHandle, state: 'TAggregateState, id: string, loaded: int64,
         cancellationToken: CancellationToken) : Task =
        task {
            if loaded = 0L then
                try
                    if isNull session then
                        do! collection.InsertOneAsync(state, InsertOneOptions(), cancellationToken).ConfigureAwait(false)
                    else
                        do! collection.InsertOneAsync(session, state, InsertOneOptions(), cancellationToken).ConfigureAwait(false)
                with :? MongoWriteException as ex when ex.WriteError.Category = ServerErrorCategory.DuplicateKey ->
                    raise (ConcurrencyException.Created(typeof<'TAggregateState>, id, ex))
            else
                let filter =
                    Builders<'TAggregateState>.Filter.And(
                        Builders<'TAggregateState>.Filter.Eq("Id", id),
                        Builders<'TAggregateState>.Filter.Eq("Version", loaded))
                let! replaced =
                    if isNull session then collection.ReplaceOneAsync(filter, state, ReplaceOptions(), cancellationToken)
                    else collection.ReplaceOneAsync(session, filter, state, ReplaceOptions(), cancellationToken)
                if replaced.MatchedCount = 0L then
                    raise (ConcurrencyException.Changed(typeof<'TAggregateState>, id, loaded))
        } :> Task

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
                let state = aggregate.GetState()
                let loaded = LoadedVersionOf aggregate
                let events =
                    match outbox with
                    | Some _ -> OutboxEvents.Of aggregate
                    | None -> [||]
                if events.Length = 0 then
                    do! MongoDBAggregateRepository.WriteAsync(collection, null, state, aggregate.Id, loaded, CancellationToken.None).ConfigureAwait(false)
                else
                    let outbox = outbox.Value
                    do! outbox.EnsureTTLIndexAsync().ConfigureAwait(false)
                    use! session = db.Client.StartSessionAsync()
                    // Set by the transaction's last attempt, the one that commits
                    let recorded = ref 0
                    let storeWithEvents = Func<IClientSessionHandle, CancellationToken, Task<unit>>(fun session cancellationToken ->
                        task {
                            try
                                do! MongoDBAggregateRepository.WriteAsync(collection, session, state, aggregate.Id, loaded, cancellationToken).ConfigureAwait(false)
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
