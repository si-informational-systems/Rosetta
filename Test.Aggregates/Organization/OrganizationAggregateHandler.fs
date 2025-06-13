namespace TestFSharp

open SI.Rosetta.Aggregates

type IOrganizationAggregateHandler =
    inherit IAggregateHandler

type OrganizationAggregateHandler(repo: IEventSourcedAggregateRepository) = 
    inherit AggregateHandler<OrganizationAggregate, OrganizationCommands, OrganizationEvents>()
    do base.AggregateRepository <- repo
    interface IOrganizationAggregateHandler
    
    override this.ExecuteAsync(command: OrganizationCommands) =
        task {
            match command with
            | OrganizationCommands.Register cmd ->
                do! this.IdempotentlyCreateAggregate cmd.Id command
        } 