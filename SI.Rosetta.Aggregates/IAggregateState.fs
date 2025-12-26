namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common

type IAggregateState =
    abstract member Id: string
    abstract member Version: int64

type IAggregateStateInstance<'TEvents
    when 'TEvents :> IAggregateEvents> =
    inherit IAggregateState
    abstract member Mutate: event: 'TEvents -> unit