namespace TestFSharp

open SI.Rosetta.Aggregates
open System.Collections.Generic

type PersonAggregateState() =
    inherit AggregateState<PersonEvents>()
    member val Name = "" with get, set
    member val Height = 0 with get, set
    member val Records = List<Record>() with get, set

    override this.ApplyEvent(ev: PersonEvents) =
        match ev with
        | PersonEvents.Registered ev ->
            this.Id <- ev.Id
            this.Name <- ev.Name

        | PersonEvents.RecordAdded ev ->
            this.Records.Add(ev.Record)

        | PersonEvents.NameChanged ev ->
            this.Name <- ev.Name

        | PersonEvents.HeightSet ev ->
            this.Height <- ev.Height