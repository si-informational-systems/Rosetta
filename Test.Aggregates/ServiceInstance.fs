namespace TestFSharp

open Microsoft.Extensions.Hosting
open System.Threading.Tasks
open System.Threading
open System
open SI.Rosetta.TestKit
open Microsoft.Extensions.Logging
open System.Diagnostics

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
                        Id = "People-5"
                        Name = "Goran"
                        Metadata = metadata
                    }
                    
                    do! personConsumer.Consume(registerPersonCmd).ConfigureAwait(false)

                    let changeName : ChangePersonName = {
                        Id = "People-5"
                        Name = "Mladen"
                        Metadata = metadata
                    }
                    
                    do! personConsumer.Consume(changeName).ConfigureAwait(false)

                    //let stopwatch = Stopwatch()
                    ////for i = 1 to 365 do
                    //let record = {
                    //    Date = DateTime.Now
                    //    Value = 366
                    //    Reference = { Id = $"Ref-{366}"; Name = "Reference" }
                    //}
                    //let addRecordCmd : AddPersonRecord = {
                    //    Id = registerPersonCmd.Id
                    //    Record = record
                    //    Metadata = metadata
                    //}
                    //stopwatch.Start()
                    //do! personConsumer.Consume(addRecordCmd).ConfigureAwait(false)
                    //stopwatch.Stop()
                    //printfn "Added record %d in %d ms" 366 stopwatch.ElapsedMilliseconds
                    //stopwatch.Reset()

                    //let orgConsumer = OrganizationConsumer(orgHandler, orgLogger)
                    //let registerOrganizationCmd : RegisterOrganization = {
                    //    Id = "Organizations-1"
                    //    Name = "WWE"
                    //    Metadata = metadata
                    //}
                    
                    //do! orgConsumer.Consume(registerOrganizationCmd).ConfigureAwait(false)
                with 
                | ex -> 
                    printfn "Error: %s" ex.Message
            }

        member this.StopAsync(cancellationToken: CancellationToken) =
            Task.CompletedTask
