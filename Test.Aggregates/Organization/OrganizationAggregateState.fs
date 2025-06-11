namespace TestFSharp

open SI.Rosetta.Aggregates

type OrganizationAggregateState() =
    inherit AggregateState<OrganizationEvents>()
    member val Name = "" with get, set

    override this.ApplyEvent(ev: OrganizationEvents) =
        match ev with
        | OrganizationEvents.Registered ev ->
            this.Id <- ev.Id
            this.Name <- ev.Name 