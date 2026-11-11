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

    /// As every store does: nothing is written over changes made after the aggregate was loaded. A member of its own:
    /// inside StoreAsync's task, the F# compiler emitted an invalid program
    static member private CheckVersion(stateType: System.Type, aggregate: IAggregate, stored: int) =
        let loaded = LoadedVersionOf aggregate
        if int64 stored <> loaded then
            raise (if loaded = 0L then ConcurrencyException.Created(stateType, aggregate.Id)
                   else ConcurrencyException.Changed(stateType, aggregate.Id, loaded))

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
                TestKitAggregateRepository.CheckVersion(typeof<'TAggregateState>, agg, events.Count)

                agg.Changes
                |> Seq.iter events.Add
                
                InMemoryStore.[agg.Id] <- events

                agg.Changes 
                |> Seq.iter ProducedEvents.Add 

                agg.Changes.Clear()
            }

    interface IEventSourcedAggregateRepository
    interface IStateBasedAggregateRepository


