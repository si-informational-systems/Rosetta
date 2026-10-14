namespace SI.Rosetta.Projections.HostBuilder

open System
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
    lifetime: IHostApplicationLifetime,
    assemblyContainingProjections: Assembly) =
    inherit BackgroundService()

    member private this.RunProjections(stoppingToken: CancellationToken) =
        task {
            do! jsProjectionsFactory.CreateCustomProjections(assemblyContainingProjections).ConfigureAwait(false)
            let! projections = projectionsFactory.CreateFromAssemblyAsync(assemblyContainingProjections).ConfigureAwait(false)
            let projectionsToRun =
                projections
                |> Seq.map (fun projection ->
                    logger.LogInformation("Starting projection {Name} on stream {StreamName}.", projection.Name, projection.SubscriptionStreamName)
                    projection.StartAsync(stoppingToken))
                |> Seq.toArray

            // Projections run until shutdown, unless one gives up resubscribing: the first one that does fails the whole app
            let! first = Task.WhenAny(projectionsToRun).ConfigureAwait(false)
            do! first.ConfigureAwait(false)
            do! Task.WhenAll(projectionsToRun).ConfigureAwait(false)
        }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            try
                do! this.RunProjections(stoppingToken).ConfigureAwait(false)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
            | ex ->
                logger.LogCritical(ex, "Projections FAILED. Stopping the application.")
                // A failed BackgroundService only stops the host, and the process would exit with code 0
                Environment.ExitCode <- 1
                lifetime.StopApplication()
        }
