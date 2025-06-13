namespace TestFSharp

open SI.Rosetta.Aggregates

type IPersonAggregateHandler =
    inherit IAggregateHandler

type PersonAggregateHandler(repo: IStateBasedAggregateRepository) = 
    inherit AggregateHandler<PersonAggregate, PersonCommands, PersonEvents>()
    do base.AggregateRepository <- repo
    interface IPersonAggregateHandler
    
    override this.ExecuteAsync(command: PersonCommands) =
        task {
            match command with
            | PersonCommands.Register cmd ->
                do! this.IdempotentlyCreateAggregate cmd.Id command
                
            | PersonCommands.ChangeName cmd ->
                do! this.IdempotentlyUpdateAggregate cmd.Id command
            | PersonCommands.SetHeight cmd ->
                do! this.IdempotentlyUpdateAggregate cmd.Id command
        }

