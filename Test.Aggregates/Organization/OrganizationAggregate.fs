namespace TestFSharp

open SI.Rosetta.Aggregates

type OrganizationAggregate() =
    inherit Aggregate<Organization, OrganizationCommands>()

    override this.Execute(command: OrganizationCommands) =
        match command with
        | OrganizationCommands.Register cmd -> 
            if this.State.Version > 0 then
                // Idempotent: nothing changes, but the event is published again, so a redelivered command records it in the outbox
                if this.IsIdempotent cmd then this.PublishedEvents.Add(this.Registered cmd)
                else raise (DomainException("Organization already registered"))
            else this.Register cmd

    member private this.Registered(cmd: RegisterOrganization) =
        OrganizationEvents.Registered { Id = cmd.Id; Name = cmd.Name; Metadata = cmd.Metadata }

    member private this.Register(cmd: RegisterOrganization) =
        let event = this.Registered cmd
        this.PublishedEvents.Add event
        this.Apply event

    member private this.IsIdempotent(cmd: RegisterOrganization) =
        this.State.Name = cmd.Name 