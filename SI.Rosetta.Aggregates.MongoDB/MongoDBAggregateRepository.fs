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
                let! _ = collection.ReplaceOneAsync(filter, aggregate.GetState(), ReplaceOptions(IsUpsert = true)).ConfigureAwait(false)
                ()
            }
