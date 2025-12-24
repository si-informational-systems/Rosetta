namespace SI.Rosetta.Aggregates.RavenDB

open Raven.Client.Documents
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

type RavenDBAggregateRepository(store: IDocumentStore) =
    interface IStateBasedAggregateRepository with
        member this.GetAsync<'TAggregate, 'TEvents 
                                    when 'TAggregate :> IAggregate 
                                    and 'TAggregate : (new : unit -> 'TAggregate)
                                    and 'TAggregate : not struct
                                    and 'TEvents :> IAggregateEvents>
            (id: string, version: int64): System.Threading.Tasks.Task<Option<'TAggregate>> = 
            task {
                use session = store.OpenAsyncSession()
                let aggregate = new 'TAggregate()
                let! aggregateState = session.LoadAsync(id)
                if isNull (box aggregateState) then return None
                else 
                    aggregate.SetState(aggregateState)
                    return Some aggregate
            }
        member this.StoreAsync(aggregate: IAggregate): System.Threading.Tasks.Task = 
            task {
                use session = store.OpenAsyncSession()
                let state = aggregate.GetState()
                
                do! session.StoreAsync(state).ConfigureAwait(false)
                do! session.SaveChangesAsync().ConfigureAwait(false)
            }
