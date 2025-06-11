namespace TestFSharp

open Microsoft.Extensions.Hosting
open System.Threading.Tasks
open System.Threading
open System
open SI.Rosetta.TestKit
open Microsoft.Extensions.Logging
open System.Diagnostics

type Disease = {
    DiseaseName: string
    SickSince: DateTime
}

type PersonalData = {
    Card: string
    SocialSecurity: string
    Diseases: Disease list
}

type Person = {
    Name: string
    PersonalData: PersonalData
}

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
                        Id = "Mladen"
                        Name = "Mladen"
                    }
                    let metadata = {
                        IssuedBy = userReference
                        TimeIssued = DateTime.Now
                    }
                    let registerPersonCmd : RegisterPerson = {
                        Id = "Persons-1"
                        Name = "John Cena"
                        Metadata = metadata
                    }
                    
                    do! personConsumer.Consume(registerPersonCmd).ConfigureAwait(false)

                    let orgConsumer = OrganizationConsumer(orgHandler, orgLogger)
                    let registerOrganizationCmd : RegisterOrganization = {
                        Id = "Organizations-1"
                        Name = "WWE"
                        Metadata = metadata
                    }
                    
                    do! orgConsumer.Consume(registerOrganizationCmd).ConfigureAwait(false)
                with 
                | ex -> 
                    printfn "Error: %s" ex.Message
            }

        member this.StopAsync(cancellationToken: CancellationToken) =
            Task.CompletedTask
