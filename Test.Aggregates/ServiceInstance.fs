namespace TestFSharp

open Microsoft.Extensions.Hosting
open System.Threading.Tasks
open System.Threading
open System
open Microsoft.Extensions.Logging

type ServiceInstance(
    serviceProvider: IServiceProvider,
    lifetime: IHostApplicationLifetime
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
                    let orgConsumer = OrganizationConsumer(orgHandler, orgLogger)
                    let userReference = {
                        Id = "User-1"
                        Name = "ADMIN"
                    }
                    let metadata = {
                        IssuedBy = userReference
                        TimeIssued = DateTime.Now
                    }

                    // Event-Sourced (KurrentDB): a new Person every run, so Test.Projections has something to project
                    let personId = sprintf "People-%s" (Guid.NewGuid().ToString("N").Substring(0, 8))
                    printfn "Registering %s as John, renaming to Johnny, setting height to 180" personId
                    do! personConsumer.Consume({ Id = personId; Name = "John"; Metadata = metadata } : RegisterPerson).ConfigureAwait(false)
                    do! personConsumer.Consume({ Id = personId; Name = "Johnny"; Metadata = metadata } : ChangePersonName).ConfigureAwait(false)
                    do! personConsumer.Consume({ Id = personId; Height = 180; Metadata = metadata } : SetPersonHeight).ConfigureAwait(false)

                    // State-based (RavenDB): registering the same Organization again on later runs is idempotent
                    printfn "Registering Organizations-1"
                    do! orgConsumer.Consume({ Id = "Organizations-1"; Name = "Rosetta"; Metadata = metadata } : RegisterOrganization).ConfigureAwait(false)

                with
                | ex ->
                    printfn "Error: %s" ex.Message

                // The sample only sends the commands above, so it stops once they're handled
                lifetime.StopApplication()
            }

        member this.StopAsync(cancellationToken: CancellationToken) =
            Task.CompletedTask
