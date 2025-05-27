namespace SI.Rosetta.Projections.TestKit

open System
open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Projections

//type TestKitSubscription() =
//    let mutable eventStream = Dictionary<uint64, obj>()
    
//    interface ISubscription with
//        member val Name = "" with get, set
//        member val StreamName = "" with get, set
//        member val EventAppearedCallback = Unchecked.defaultof<Func<obj, uint64, Task>> with get, set
        
//        member this.StartAsync fromCheckpoint =
//            task {
//                for KeyValue(key, value) in eventStream do
//                    if key >= fromCheckpoint then
//                        let callback = (this :> ISubscription).EventAppearedCallback
//                        do! callback.Invoke(value, key)
//            } :> Task

//    member this.LoadEvents(events: obj[]) =
//        eventStream <- Dictionary<uint64, obj>()
//        let mutable i = 0UL
//        for e in events do
//            i <- i + 1UL
//            eventStream.Add(i, e)
