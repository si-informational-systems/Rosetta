namespace SI.Rosetta.Projections.TestKit

open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Projections
open SI.Rosetta.Common

type TestKitSubscription<'TEvent when 'TEvent :> IAggregateEvents>() =
    let mutable inMemoryEventStream = Dictionary<uint64, 'TEvent>()
    let mutable eventReceived: 'TEvent * uint64 -> Task<unit> = fun (ev, cp) -> Task.FromResult<unit>()

    interface ISubscription<'TEvent> with
        member val Name = "" with get, set
        member val StreamName = "" with get, set
        // Its events are typed already, so none are unknown
        member val SkipUnknownEvents = false with get, set
        member this.EventReceived
            with get() = eventReceived
            and set(value) = eventReceived <- value        
        member this.StartAsync(fromCheckpoint, _cancellationToken) =
            task {
                // One at a time, in order, as a subscription delivers them
                for kvp in inMemoryEventStream |> Seq.filter (fun kvp -> kvp.Key >= fromCheckpoint) |> Seq.sortBy (fun kvp -> kvp.Key) do
                    do! eventReceived(kvp.Value, kvp.Key).ConfigureAwait(false)
            } :> Task

    member this.StoreEvents(events: 'TEvent[]) =
        inMemoryEventStream <- Dictionary<uint64, 'TEvent>()
        let mutable i = 0UL
        for e in events do
            i <- i + 1UL
            inMemoryEventStream.Add(i, e)
