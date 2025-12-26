namespace SI.Rosetta.Projections

open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Common

type IProjection =
    abstract member Name: string with get, set
    abstract member SubscriptionStreamName: string with get, set
    abstract member Checkpoint: Checkpoint with get, set
    abstract member StartAsync: unit -> Task

type IProjectionInstance<'TEvent when 'TEvent :> IAggregateEvents> =
    inherit IProjection
    abstract member Subscription: ISubscription<'TEvent> with get, set
    abstract member Handlers: IEnumerable<IProjectionHandler<'TEvent>> with get, set
    abstract member ProjectAsync: ('TEvent * uint64 -> Task<unit>) with get

