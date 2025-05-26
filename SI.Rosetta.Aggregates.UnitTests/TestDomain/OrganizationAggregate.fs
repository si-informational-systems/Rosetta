namespace SI.Rosetta.Aggregates.UnitTests

open SI.Rosetta.Aggregates

[<AllowNullLiteral>]
type OrganizationAggregate() =
    inherit Aggregate<OrganizationAggregateState, OrganizationCommands, OrganizationEvents>()

    override this.Execute(command: OrganizationCommands) =
        match command with
        | OrganizationCommands.Register cmd -> 
            if this.State.Version > 0 then
                if this.IsIdempotent cmd then ()
                else raise (DomainException("Organization already registered"))
            else this.Register cmd

        | OrganizationCommands.ChangeName cmd -> 
            if this.IsIdempotent cmd then ()
            else this.ChangeName cmd

    member private this.Register(cmd: RegisterOrganization) =
        let event = OrganizationEvents.Registered { Id = cmd.Id; Name = cmd.Name }
        this.PublishedEvents.Add event
        this.Apply event

    member private this.ChangeName(cmd: ChangeOrganizationName) =
        let event = OrganizationEvents.NameChanged { Id = cmd.Id; Name = cmd.Name }
        this.Apply event

    member private this.IsIdempotent(cmd: RegisterOrganization) =
        this.State.Name = cmd.Name

    member private this.IsIdempotent(cmd: ChangeOrganizationName) =
        this.State.Name = cmd.Name
