namespace SI.Rosetta.Aggregates.TestKit

open System.Collections.Concurrent
open System.Collections.Generic
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

type TestKitAggregateRepository() =
    let InMemoryStore = ConcurrentDictionary<obj, List<IAggregateEvents>>()
    let ProducedEvents = List<IAggregateEvents>()

    let LoadEvents key =
        if not (InMemoryStore.ContainsKey key) then
            List<IAggregateEvents>()
        else
            InMemoryStore.[key]

    member this.GetProducedEvents = ProducedEvents

    member this.SeedEvents(id: string, events: IAggregateEvents array) =
        if events.Length > 0 then 
            let list = List<IAggregateEvents> events
            InMemoryStore.[id] <- list
    
    interface IAggregateRepository with
        member this.GetAsync<'TAggregate, 'TAggregateState, 'TEvents
                        when 'TAggregate : (new : unit -> 'TAggregate) 
                        and 'TAggregate :> IAggregate<'TAggregateState>
                        and 'TAggregate : not struct
                        and 'TAggregateState : (new : unit -> 'TAggregateState)
                        and 'TAggregateState :> IAggregateStateInstance<'TEvents>
                        and 'TEvents :> IAggregateEvents>
            (id: string, version: int64) =
            task {
                if not (InMemoryStore.ContainsKey id) then
                    return None
                else
                    let state = new 'TAggregateState()
                    
                    let events = InMemoryStore.[id]
                    events 
                    |> Seq.takeWhile (fun _ -> state.Version <> version)
                    |> Seq.iter (fun ev -> 
                        state.Mutate(ev :?> 'TEvents))
                            
                    let agg = new 'TAggregate()
                    agg.SetState state
                    return Some agg
            }
            
        member this.StoreAsync<'TAggregate, 'TAggregateState
                            when 'TAggregate :> IAggregate<'TAggregateState>>
            (agg: 'TAggregate) =
            task {
                let events = LoadEvents agg.Id
                
                agg.Changes 
                |> Seq.iter events.Add
                
                InMemoryStore.[agg.Id] <- events

                agg.Changes 
                |> Seq.iter ProducedEvents.Add 

                agg.Changes.Clear()
            }

    interface IEventSourcedAggregateRepository
    interface IStateBasedAggregateRepository


