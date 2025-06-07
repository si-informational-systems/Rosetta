namespace SI.Rosetta.Aggregates.UnitTests

open SI.Rosetta.Aggregates


type OrganizationAggregateState() =
    inherit AggregateState<OrganizationEvents>()
    let mutable name = ""
    let mutable height = 0

    member this.Name 
        with get() = name
        and set value = name <- value

    member this.Height 
        with get() = height
        and set value = height <- value

    override this.ApplyEvent(ev: OrganizationEvents) =
        match ev with
        | OrganizationEvents.Registered ev ->
            this.Id <- ev.Id
        | OrganizationEvents.NameChanged ev ->
            this.Name <- ev.Name
