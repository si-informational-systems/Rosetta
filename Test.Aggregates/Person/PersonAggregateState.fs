namespace TestFSharp

open SI.Rosetta.Aggregates
open System.Collections.Generic

type PersonAggregateState() =
    inherit AggregateState<PersonEvents>()
    member val Name = "" with get, set
    member val Height = 0 with get, set

    override this.ApplyEvent(ev: PersonEvents) =
        match ev with
        | PersonEvents.Registered ev ->
            this.Id <- ev.Id
            this.Name <- ev.Name

        | PersonEvents.NameChanged ev ->
            this.Name <- ev.Name

        | PersonEvents.NameChangedToJohn ev -> ()

        | PersonEvents.HeightSet ev ->
            this.Height <- ev.Height