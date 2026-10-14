namespace SI.Rosetta.Aggregates.MongoDB

open MongoDB.Driver
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

/// Stores the AggregateState as a document in the pluralized AggregateState type name collection.
/// Version is persisted as a regular field of the document.
type MongoDBAggregateRepository(db: IMongoDatabase) =
    member private this.GetCollection<'TAggregateState>() =
        db.GetCollection<'TAggregateState>(PluralizeName(typeof<'TAggregateState>.Name))

    interface IStateBasedAggregateRepository with
        member this.GetAsync<'TAggregate, 'TAggregateState, 'TEvents
                                    when 'TAggregate :> IAggregate<'TAggregateState>
                                    and 'TAggregate : (new : unit -> 'TAggregate)
                                    and 'TAggregate : not struct
                                    and 'TAggregateState : (new : unit -> 'TAggregateState)
                                    and 'TAggregateState :> IAggregateStateInstance<'TEvents>
                                    and 'TEvents :> IAggregateEvents>
            (id: string, version: int64): System.Threading.Tasks.Task<Option<'TAggregate>> =
            task {
                let collection = this.GetCollection<'TAggregateState>()
                let filter = Builders<'TAggregateState>.Filter.Eq("Id", id)
                let! aggregateState = collection.Find(filter).FirstOrDefaultAsync().ConfigureAwait(false)
                if isNull (box aggregateState) then return None
                else
                    let aggregate = new 'TAggregate()
                    aggregate.SetState(aggregateState)
                    return Some aggregate
            }

        member this.StoreAsync<'TAggregate, 'TAggregateState
                    when 'TAggregate :> IAggregate<'TAggregateState>>
                    (aggregate: 'TAggregate): System.Threading.Tasks.Task =
            task {
                let collection = this.GetCollection<'TAggregateState>()
                let filter = Builders<'TAggregateState>.Filter.Eq("Id", aggregate.Id)
                let! _ = collection.ReplaceOneAsync(filter, aggregate.GetState(), ReplaceOptions(IsUpsert = true)).ConfigureAwait(false)
                ()
            }
