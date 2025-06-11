namespace TestFSharp

open Microsoft.Extensions.Logging

type OrganizationConsumer(handler: IOrganizationAggregateHandler, logger: ILogger<OrganizationConsumer>) =
    inherit AggregateConsumerBase<OrganizationCommands>()
    
    member this.Consume(cmd: RegisterOrganization) =
        task {
            do! this.TryHandle(OrganizationCommands.Register cmd, handler, logger).ConfigureAwait(false)
        } 