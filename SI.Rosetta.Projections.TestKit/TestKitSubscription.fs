namespace SI.Rosetta.Projections.TestKit

open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Projections
open SI.Rosetta.Common

type TestKitSubscription<'TEvent when 'TEvent :> IEvents>() =
    let mutable inMemoryEventStream = Dictionary<uint64, 'TEvent>()
    let mutable eventReceived: 'TEvent * uint64 -> Task<unit> = fun (ev, cp) -> Task.FromResult<unit>()

    interface ISubscription<'TEvent> with
        member val Name = "" with get, set
        member val StreamName = "" with get, set
        member this.EventReceived
            with get() = eventReceived
            and set(value) = eventReceived <- value        
        member this.StartAsync fromCheckpoint =
            task {
                let tasks = 
                    inMemoryEventStream
                    |> Seq.filter (fun kvp -> kvp.Key >= fromCheckpoint)
                    |> Seq.map (fun kvp -> eventReceived(kvp.Value, kvp.Key))
                    |> Seq.toArray
                let! _ = Task.WhenAll(tasks)
                return ()
            } :> Task

    member this.StoreEvents(events: 'TEvent[]) =
        inMemoryEventStream <- Dictionary<uint64, 'TEvent>()
        let mutable i = 0UL
        for e in events do
            i <- i + 1UL
            inMemoryEventStream.Add(i, e)
