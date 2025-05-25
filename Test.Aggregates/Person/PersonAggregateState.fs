namespace TestFSharp

open SI.Rosetta.Aggregates
open System

[<AllowNullLiteral>]
type PersonAggregateState() =
    inherit AggregateState<PersonEvents>()
    let mutable name = ""
    let mutable height = 0

    member this.Name 
        with get() = name
        and set value = name <- value

    member this.Height 
        with get() = height
        and set value = height <- value

    override this.ApplyEvent(ev: PersonEvents) =
        match ev with
        | PersonEvents.Registered ev ->
            this.Id <- ev.Id
        | PersonEvents.NameChanged ev ->
            this.Name <- ev.Name
        | PersonEvents.HeightSet ev ->
            this.Height <- ev.Height