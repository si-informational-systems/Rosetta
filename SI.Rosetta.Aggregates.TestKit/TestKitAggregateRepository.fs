module TestKitAggregateRepository

open System.Collections.Concurrent
open System.Collections.Generic
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

type TestKitAggregateRepository() =
    let DataStore = ConcurrentDictionary<string, List<IAggregateEvents>>()
    let ProducedEvents = List<IAggregateEvents>()

    let LoadEvents key =
        if not (DataStore.ContainsKey key) then
            List<IAggregateEvents>()
        else
            DataStore.[key]

    member this.Preload(id: string, events: IAggregateEvents array) =
        if events.Length > 0 then 
            let list = List<IAggregateEvents>(events)
            DataStore.[id] <- list
    
    interface IAggregateRepository with
        member this.GetAsync<'TAggregate, 'TEvents 
                            when 'TAggregate :> IAggregate 
                            and 'TAggregate : (new : unit -> 'TAggregate)
                            and 'TAggregate : null
                            and 'TEvents :> IAggregateEvents>
            (id: string, version: int) =
            task {
                if not (DataStore.ContainsKey id) then
                    return null
                else
                    let aggregateType = typeof<'TAggregate>
                    let state = AggregateStateFactory.CreateStateFor<'TEvents> aggregateType
                    
                    let events = DataStore.[id]
                    events 
                    |> Seq.takeWhile (fun _ -> state.Version <> version)
                    |> Seq.iter (fun ev -> 
                        state.Mutate(ev :?> 'TEvents))
                            
                    let agg = new 'TAggregate()
                    agg.SetState state
                    return agg
            }
            
        member this.StoreAsync(agg: IAggregate) =
            task {
                let events = LoadEvents agg.Id
                
                agg.Changes 
                |> Seq.iter events.Add
                
                DataStore.[agg.Id] <- events

                agg.Changes 
                |> Seq.iter ProducedEvents.Add 

                agg.Changes.Clear()
            }


