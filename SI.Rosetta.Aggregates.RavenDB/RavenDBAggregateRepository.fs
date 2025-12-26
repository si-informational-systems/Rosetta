namespace SI.Rosetta.Aggregates.RavenDB

open Raven.Client.Documents
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

type RavenDBAggregateRepository(store: IDocumentStore) =
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
                use session = store.OpenAsyncSession()
                let aggregate = new 'TAggregate()
                let! aggregateState = session.LoadAsync(id)
                if isNull (box aggregateState) then return None
                else 
                    aggregate.SetState(aggregateState)
                    return Some aggregate
            }
        member this.StoreAsync<'TAggregate, 'TAggregateState
                    when 'TAggregate :> IAggregate<'TAggregateState>>
                    (aggregate: 'TAggregate): System.Threading.Tasks.Task = 
            task {
                use session = store.OpenAsyncSession()
                let state = aggregate.GetState()
                
                do! session.StoreAsync(state).ConfigureAwait(false)
                do! session.SaveChangesAsync().ConfigureAwait(false)
            }
