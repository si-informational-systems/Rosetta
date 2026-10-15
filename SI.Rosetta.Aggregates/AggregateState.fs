namespace SI.Rosetta.Aggregates

open System
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
        member this.EventsType = typeof<'TEvents>
        member this.Mutate event =
            match event with
            | :? 'TEvents as ev ->
                this.ApplyEvent ev
                this.Version <- this.Version + 1L
            | _ ->
                raise (ArgumentException(
                    sprintf "%s is built from %s, so it can't apply %s." (this.GetType().Name) typeof<'TEvents>.Name (event.GetType().FullName)))

    interface IAggregateStateInstance<'TEvents>

    abstract ApplyEvent: ev: 'TEvents -> unit
