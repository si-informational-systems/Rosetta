namespace SI.Rosetta.Projections

open System.Threading
open System.Threading.Tasks
open SI.Rosetta.Common

type ISubscription<'TEvent when 'TEvent :> IAggregateEvents> =
    abstract member Name: string with get, set
    abstract member StreamName: string with get, set
    /// Runs until the cancellationToken is canceled (completes normally) or the subscription gives up (faults)
    abstract member StartAsync: startingCheckpoint: uint64 * cancellationToken: CancellationToken -> Task
    abstract member EventReceived: ('TEvent * uint64 -> Task<unit>) with get, set
    /// Skip events 'TEvent has no case for, instead of failing on them (set from the projection's [<SkipUnknownEvents>])
    abstract member SkipUnknownEvents: bool with get, set
