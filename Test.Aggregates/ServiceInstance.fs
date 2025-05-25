namespace TestFSharp

open Microsoft.Extensions.Hosting
open System.Threading.Tasks
open System.Threading
open System
open SI.Rosetta.TestKit

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
                    let personA : Person = {
                        Name = "Mladen"
                        PersonalData = {
                            Card = "CARD_A"
                            SocialSecurity = "1234"
                            Diseases = [
                                {
                                    DiseaseName = "Fever"
                                    SickSince = DateTime.MinValue.AddDays(1)
                                };
                                {
                                    DiseaseName = "Flu"
                                    SickSince = DateTime.MinValue
                                }
                            ]
                        }
                    }
                    let personB : Person = {
                        Name = "Goran"
                        PersonalData = {
                            Card = "CARD_B"
                            SocialSecurity = "12345"
                            Diseases = [
                                {
                                    DiseaseName = "Fever1"
                                    SickSince = DateTime.MinValue.AddDays(2)
                                };
                                {
                                    DiseaseName = "Flu1"
                                    SickSince = DateTime.MinValue.AddDays(1)
                                }
                            ]
                        }
                    }
                    let diff = ObjectComparer.DeepCompare personA personB
                    printf "%s" diff
                    return ()

                    //printfn "Starting AGGREGATES SERVICE"
                    //let interactor = serviceProvider.GetService(typeof<IPersonAggregateHandler>) :?> IPersonAggregateHandler
                    //let logger = serviceProvider.GetService(typeof<ILogger<PersonConsumer>>) :?> ILogger<PersonConsumer>

                    //let consumer = PersonConsumer(interactor, logger)
                    //let userReference = {
                    //    Id = "Mladen"
                    //    Name = "Mladen"
                    //}
                    //let metadata = {
                    //    IssuedBy = userReference
                    //    TimeIssued = DateTime.Now
                    //}
                    //let registerCmd : RegisterPerson = { 
                    //    Id = "Persons-4"
                    //    Name = "TEST"
                    //    Metadata = metadata
                    //}

                    ////Benchmark register command
                    //let sw = Stopwatch.StartNew()
                    //let memBefore = GC.GetTotalMemory(true)
                    //do! consumer.Consume(registerCmd)
                    //let memAfter = GC.GetTotalMemory(true)
                    //sw.Stop()
                    //printfn "Register Command - Time: %dms, Memory: %dKB" sw.ElapsedMilliseconds ((memAfter - memBefore) / 2024L)

                    //Benchmark 100 name changes
                    //for i in 0..500 do
                    //let userReference = {
                    //    Id = "Mladen"
                    //    Name = "Mladen2"
                    //}
                    //let metadata = {
                    //    IssuedBy = userReference
                    //    TimeIssued = DateTime.Now
                    //}
                    //let changeName : ChangePersonName = { 
                    //    Id = "Persons-3"
                    //    Name = "Mladen"
                    //    Metadata = metadata
                    //}
                    //let sw = Stopwatch.StartNew()
                    //let memBefore = GC.GetTotalMemory(true)
                    //do! consumer.Consume(changeName)
                    //let memAfter = GC.GetTotalMemory(true)
                    //sw.Stop()
                    //printfn "Change Name Command - Time: %dms, Memory: %dKB" sw.ElapsedMilliseconds ((memAfter - memBefore) / 1024L)
                        
                with 
                | ex -> 
                    printfn "Error: %s" ex.Message
            }

        member this.StopAsync(cancellationToken: CancellationToken) =
            Task.CompletedTask
