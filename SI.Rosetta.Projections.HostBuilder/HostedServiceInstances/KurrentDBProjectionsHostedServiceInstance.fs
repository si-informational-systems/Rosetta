namespace SI.Rosetta.Projections.HostBuilder

open System
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open System.Reflection
open System.Threading
open System.Threading.Tasks
open SI.Rosetta.Common
open SI.Rosetta.Projections
open SI.Rosetta.Projections.KurrentDB

type KurrentDBProjectionsHostedServiceInstance(
    logger: ILogger<KurrentDBProjectionsHostedServiceInstance>,
    projectionsFactory: IProjectionsFactory,
    jsProjectionsFactory: IKurrentDBCustomJSProjectionsFactory,
    lifetime: IHostApplicationLifetime,
    registration: ProjectionsRegistration) =
    inherit BackgroundService()

    new(logger, projectionsFactory, jsProjectionsFactory, lifetime, assemblyContainingProjections: Assembly) =
        new KurrentDBProjectionsHostedServiceInstance(
            logger, projectionsFactory, jsProjectionsFactory, lifetime, ProjectionsRegistration.OfAssembly assemblyContainingProjections)

    /// The custom projections of every ICustomProjectionEvents DU in the assemblies, and of the named projections that
    /// project one (their stream constants are in the projection's assembly)
    member private this.CustomProjections =
        Seq.append
            (registration.Assemblies |> Seq.collect CustomProjectionDiscovery.DiscoverCustomProjections)
            (registration.NamedProjections
             |> Seq.filter (fun named -> typeof<ICustomProjectionEvents>.IsAssignableFrom named.EventsType)
             |> Seq.map (fun named -> CustomProjectionDiscovery.DiscoverCustomProjection named.ProjectionType.Assembly named.EventsType))
        |> Seq.distinctBy (fun parameters -> parameters.ProjectionName)
        |> Seq.toArray

    member private this.RunProjections(stoppingToken: CancellationToken) =
        task {
            do! jsProjectionsFactory.CreateCustomProjections(this.CustomProjections).ConfigureAwait(false)
            let! projections = registration.CreateProjectionsAsync(projectionsFactory).ConfigureAwait(false)
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
