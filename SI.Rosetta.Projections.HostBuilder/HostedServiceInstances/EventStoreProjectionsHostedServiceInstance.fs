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
            let! projections = projectionsFactory.CreateFromAssemblyAsync(assemblyContainingProjections).ConfigureAwait(false)
            let projectionsToRun =
                projections
                |> Seq.map (fun projection -> 
                    logger.LogInformation($"Starting projection {projection.Name} on stream {projection.SubscriptionStreamName}.")
                    projection.StartAsync())
                |> Seq.toArray
                
            do! Task.WhenAll(projectionsToRun).ConfigureAwait(false)
        }
        
    interface IHostedService with
        member this.StartAsync(cancellationToken: CancellationToken) =
            task {
                do! jsProjectionsFactory.CreateCustomProjections(assemblyContainingProjections).ConfigureAwait(false)
                do! this.RunProjections().ConfigureAwait(false)
            }
            
        member this.StopAsync(cancellationToken: CancellationToken) =
            Task.CompletedTask

