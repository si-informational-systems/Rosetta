namespace TestFSharp

open Microsoft.Extensions.Hosting
open System.Threading.Tasks
open System.Threading
open System
open Microsoft.Extensions.Logging

type ServiceInstance(
    serviceProvider: IServiceProvider
    ) =
    interface IHostedService with
        member this.StartAsync(cancellationToken: CancellationToken) =
            task {
                try
                    printfn "Starting AGGREGATES SERVICE"
                    let personHandler = serviceProvider.GetService(typeof<IPersonAggregateHandler>) :?> IPersonAggregateHandler
                    let personLogger = serviceProvider.GetService(typeof<ILogger<PersonConsumer>>) :?> ILogger<PersonConsumer>
                    let orgHandler = serviceProvider.GetService(typeof<IOrganizationAggregateHandler>) :?> IOrganizationAggregateHandler
                    let orgLogger = serviceProvider.GetService(typeof<ILogger<OrganizationConsumer>>) :?> ILogger<OrganizationConsumer>

                    let personConsumer = PersonConsumer(personHandler, personLogger)
                    let userReference = {
                        Id = "User-1"
                        Name = "Mark"
                    }
                    let metadata = {
                        IssuedBy = userReference
                        TimeIssued = DateTime.Now
                    }

                    let changeName : ChangePersonName = {
                        Id = "People-1"
                        Name = "Mark"
                        Metadata = metadata
                    }
                    
                    do! personConsumer.Consume(changeName).ConfigureAwait(false)

                with 
                | ex -> 
                    printfn "Error: %s" ex.Message
            }

        member this.StopAsync(cancellationToken: CancellationToken) =
            Task.CompletedTask
