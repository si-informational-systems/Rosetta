namespace SI.Rosetta.Aggregates

open System.Collections.Generic
open SI.Rosetta.Common

type IAggregate =
    abstract member Id: string
    abstract member Version: int
    abstract member Changes: List<IAggregateEvents>
    abstract member PublishedEvents: List<IAggregateEvents>
    abstract member SetState: state: obj -> unit
    abstract member GetState: unit -> obj

type IAggregateInstance<'TCommands when 'TCommands :> IAggregateCommands> =
    inherit IAggregate
    abstract member Execute: command: 'TCommands -> unit 