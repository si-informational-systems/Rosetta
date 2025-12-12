namespace TestFSharp

open SI.Rosetta.Aggregates

type OrganizationAggregate() =
    inherit Aggregate<Organization, OrganizationCommands, OrganizationEvents>()

    override this.Execute(command: OrganizationCommands) =
        match command with
        | OrganizationCommands.Register cmd -> 
            if this.State.Version > 0 then
                if this.IsIdempotent cmd then ()
                else raise (DomainException("Organization already registered"))
            else this.Register cmd

    member private this.Register(cmd: RegisterOrganization) =
        let event = OrganizationEvents.Registered { Id = cmd.Id; Name = cmd.Name; Metadata = cmd.Metadata }
        this.PublishedEvents.Add event
        this.Apply event

    member private this.IsIdempotent(cmd: RegisterOrganization) =
        this.State.Name = cmd.Name 