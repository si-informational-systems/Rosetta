namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common

[<AbstractClass>]
type AggregateState<'TEvents when 'TEvents :> IAggregateEvents>() =
    member val Id : obj = null with get, set
    member val Version = 0 with get, set
    
    interface IAggregateState with
        member this.Id = this.Id
        member this.Version = this.Version

    member this.Mutate event =
        this.ApplyEvent event
        this.Version <- this.Version + 1

    abstract ApplyEvent: ev: 'TEvents -> unit