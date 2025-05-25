namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common

[<AbstractClass>]
[<AllowNullLiteral>]
type AggregateState<'TEvents when 'TEvents :> IEvents>() =
    let mutable id = ""
    let mutable version = 0
    
    interface IAggregateState with
        member this.Id = id
        member this.Version = version

    member this.Id
        with get() = id
        and set value = id <- value
    member this.Version = version

    member this.Mutate event =
        this.ApplyEvent event
        version <- version + 1

    abstract ApplyEvent: ev: 'TEvents -> unit