namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common

[<AbstractClass>]
type AggregateState<'TEvents when 'TEvents :> IAggregateEvents>() =
    member val Id : string = null with get, set
    member val Version = 0L with get, set
    
    interface IAggregateState with
        member this.Id = this.Id
        member this.Version
            with get() = this.Version
            and set value = this.Version <- value

    interface IAggregateStateInstance<'TEvents> with
        member this.Mutate event =
            this.ApplyEvent event
            this.Version <- this.Version + 1L

    abstract ApplyEvent: ev: 'TEvents -> unit