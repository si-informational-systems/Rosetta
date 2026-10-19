namespace SI.Rosetta.Aggregates

open System
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

[<AutoOpen>]
module AggregateNames =
    /// An aggregate's name in traces, metrics and the outbox: its type name without "Aggregate" (PersonAggregate is Person)
    let AggregateNameOf (aggregateType: Type) =
        aggregateType.Name.Replace("Aggregate", "", StringComparison.InvariantCultureIgnoreCase)
