namespace SI.Rosetta.Aggregates

open System.Collections.Generic
open SI.Rosetta.Common

[<AllowNullLiteral>]
type IAggregate =
    abstract member Id: string
    abstract member Version: int
    abstract member Changes: List<IEvents>
    abstract member PublishedEvents: List<IEvents>
    abstract member SetState: state: obj -> unit 

[<AllowNullLiteral>]
type IAggregateInstance<'TCommands when 'TCommands :> ICommands> =
    inherit IAggregate
    abstract member Execute: command: 'TCommands -> unit 