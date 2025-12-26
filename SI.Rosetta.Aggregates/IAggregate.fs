namespace SI.Rosetta.Aggregates

open System.Collections.Generic
open SI.Rosetta.Common

type IAggregate<'TAggregateState when 'TAggregateState :> IAggregateState> =
    abstract member Id: string
    abstract member Version: int64
    abstract member Changes: List<IAggregateEvents>
    abstract member PublishedEvents: List<IAggregateEvents>
    abstract member SetState: state: 'TAggregateState -> unit
    abstract member GetState: unit -> 'TAggregateState

type IAggregateInstance<'TAggregateState, 'TCommands 
    when 'TAggregateState :> IAggregateState
    and 'TCommands :> IAggregateCommands> =
    inherit IAggregate<'TAggregateState>
    abstract member Execute: command: 'TCommands -> unit 