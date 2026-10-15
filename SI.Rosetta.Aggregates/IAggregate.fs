namespace SI.Rosetta.Aggregates

open System.Collections.Generic
open SI.Rosetta.Common

type IAggregate =
    abstract member Id: string
    abstract member Version: int64
    abstract member Changes: List<IAggregateEvents>
    abstract member PublishedEvents: List<IAggregateEvents>

type IAggregate<'TAggregateState when 'TAggregateState :> IAggregateState> =
    inherit IAggregate
    abstract member GetState: unit -> 'TAggregateState
