namespace TestFSharp

open SI.Rosetta.Aggregates

type Person() =
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

        | PersonEvents.HeightSet ev ->
            this.Height <- ev.Height