namespace SI.Rosetta.Projections.HostBuilder

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System.Reflection
open System.Threading
open System.Threading.Tasks
open SI.Rosetta.Projections
open SI.Rosetta.Projections.EventStore

type EventStoreProjectionsHostedServiceInstance(
    logger: ILogger<EventStoreProjectionsHostedServiceInstance>,
    projectionsFactory: IProjectionsFactory,
    jsProjectionsFactory: IESCustomJSProjectionsFactory,
    assemblyContainingProjections: Assembly) =
            
    member private this.RunProjections() =
        task {
            let! projections = projectionsFactory.CreateFromAssemblyAsync(assemblyContainingProjections)
            let projectionsToRun =
                projections
                |> Seq.map (fun projection -> 
                    logger.LogInformation($"Starting projection {projection.Name} on stream {projection.SubscriptionStreamName}.")
                    projection.StartAsync())
                |> Seq.toArray
                
            do! Task.WhenAll(projectionsToRun)
        }
        
    interface IHostedService with
        member this.StartAsync(cancellationToken: CancellationToken) =
            task {
                do! jsProjectionsFactory.CreateCustomProjections(assemblyContainingProjections)
                do! this.RunProjections()
            }
            
        member this.StopAsync(cancellationToken: CancellationToken) =
            Task.CompletedTask

