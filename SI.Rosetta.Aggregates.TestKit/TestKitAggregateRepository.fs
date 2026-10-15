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
        member this.GetAsync<'TAggregateState
                        when 'TAggregateState : (new : unit -> 'TAggregateState)
                        and 'TAggregateState :> IAggregateState>
            (id: string, version: int64) =
            task {
                if not (InMemoryStore.ContainsKey id) then
                    return None
                else
                    let state = new 'TAggregateState()

                    let events = InMemoryStore.[id]
                    events
                    |> Seq.takeWhile (fun _ -> state.Version <> version)
                    |> Seq.iter state.Mutate

                    return Some state
            }

        member this.StoreAsync<'TAggregateState
                            when 'TAggregateState :> IAggregateState>
            (agg: IAggregate<'TAggregateState>) =
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


