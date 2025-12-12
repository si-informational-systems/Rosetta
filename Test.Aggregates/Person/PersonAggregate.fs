namespace TestFSharp

open SI.Rosetta.Aggregates

type PersonAggregate() =
    inherit Aggregate<PersonAggregateState, PersonCommands, PersonEvents>()

    override this.Execute(command: PersonCommands) =
        match command with
        | PersonCommands.Register cmd -> 
            if this.State.Version > 0 then
                if this.IsIdempotent cmd then ()
                else raise (DomainException("Person already registered"))
            else this.Register cmd

        | PersonCommands.ChangeName cmd -> 
            if this.IsIdempotent cmd then ()
            else this.ChangeName cmd

        | PersonCommands.SetHeight cmd -> 
            if this.IsIdempotent cmd then ()
            else this.SetHeight cmd

    member private this.Register(cmd: RegisterPerson) =
        let event = PersonEvents.Registered { Id = cmd.Id; Name = cmd.Name; Metadata = cmd.Metadata }
        this.PublishedEvents.Add event
        this.Apply event

    member private this.ChangeName(cmd: ChangePersonName) =
        let event = PersonEvents.NameChanged { Id = cmd.Id; Name = cmd.Name; Metadata = cmd.Metadata }
        this.Apply event
        if (this.State.Name = "John") then
            let JohnEvent = PersonEvents.NameChangedToJohn { Id = cmd.Id; Name = cmd.Name; Metadata = cmd.Metadata }
            this.Apply JohnEvent
            this.PublishedEvents.Add(JohnEvent)

    member private this.SetHeight(cmd: SetPersonHeight) =
        let event = PersonEvents.HeightSet { Id = cmd.Id; Height = cmd.Height; Metadata = cmd.Metadata }
        this.Apply event

    member private this.IsIdempotent(cmd: RegisterPerson) =
        this.State.Name = cmd.Name

    member private this.IsIdempotent(cmd: ChangePersonName) =
        this.State.Name = cmd.Name

    member private this.IsIdempotent(cmd: SetPersonHeight) =
        this.State.Height = cmd.Height