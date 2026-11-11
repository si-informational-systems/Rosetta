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

/// A subscription whose stream can be replaced under its projection, e.g. a custom projection's cp- stream linked again
/// from the start. Checked when the projection starts: a projection whose read models were built from what the stream
/// no longer holds is rebuilt, as when its [<ProjectionVersion>] goes up
type ISubscriptionHistory =
    /// Why the read models built up to checkpoint (one-based) no longer match the stream, or None when they do
    abstract member ReplacedSinceAsync: checkpoint: uint64 -> Task<string option>
