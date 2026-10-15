namespace SI.Rosetta.Aggregates.UnitTests

open SI.Rosetta.Aggregates

type IOrganizationAggregateHandler =
    inherit IAggregateHandler

type OrganizationAggregateHandler(repo: IEventSourcedAggregateRepository) = 
    inherit AggregateHandler<OrganizationAggregate, OrganizationCommands>()
    do base.AggregateRepository <- repo
    interface IOrganizationAggregateHandler
    
    override this.ExecuteAsync(command: OrganizationCommands) =
        task {
            match command with
            | OrganizationCommands.Register cmd ->
                do! this.IdempotentlyCreateAggregate cmd.Id command
                
            | OrganizationCommands.ChangeName cmd ->
                do! this.IdempotentlyUpdateAggregate cmd.Id command
        }

