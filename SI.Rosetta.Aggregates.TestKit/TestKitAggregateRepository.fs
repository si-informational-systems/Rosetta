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
        member this.GetAsync<'TAggregate, 'TEvents 
                            when 'TAggregate :> IAggregate 
                            and 'TAggregate : (new : unit -> 'TAggregate)
                            and 'TAggregate : not struct
                            and 'TEvents :> IAggregateEvents>
            (id: obj, version: int) =
            task {
                if not (InMemoryStore.ContainsKey id) then
                    return None
                else
                    let aggregateType = typeof<'TAggregate>
                    let state = AggregateStateFactory.CreateStateFor<'TEvents> aggregateType
                    
                    let events = InMemoryStore.[id]
                    events 
                    |> Seq.takeWhile (fun _ -> state.Version <> version)
                    |> Seq.iter (fun ev -> 
                        state.Mutate(ev :?> 'TEvents))
                            
                    let agg = new 'TAggregate()
                    agg.SetState state
                    return Some agg
            }
            
        member this.StoreAsync(agg: IAggregate) =
            task {
                let events = LoadEvents agg.Id
                
                agg.Changes 
                |> Seq.iter events.Add
                
                InMemoryStore.[agg.Id] <- events

                agg.Changes 
                |> Seq.iter ProducedEvents.Add 

                agg.Changes.Clear()
            }


