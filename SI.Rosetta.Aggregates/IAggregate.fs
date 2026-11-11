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

    /// The version the aggregate was loaded at (0 for a new one): each of its Changes added one. What the state-based
    /// repositories check the stored state against, so a command never overwrites another's changes
    let LoadedVersionOf (aggregate: IAggregate) =
        aggregate.Version - int64 aggregate.Changes.Count
