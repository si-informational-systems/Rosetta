namespace SI.Rosetta.Projections.HostBuilder

open System
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SI.Rosetta.Common
open SI.Rosetta.Projections
open SI.Rosetta.Projections.KurrentDB

/// Runs every projection until the application stops. A projection that fails retries on its own, and one that can't
/// go on stops alone: either way the others and the application keep running, and the "rosetta-projections" health
/// check reports it. Unless the registration says StopApplicationOnFailure: then a projection that faults, stops or
/// can't start stops the application, with Environment.ExitCode 1
type KurrentDBProjectionsHostedServiceInstance(
    logger: ILogger<KurrentDBProjectionsHostedServiceInstance>,
    projectionsFactory: IProjectionsFactory,
    jsProjectionsFactory: IKurrentDBCustomJSProjectionsFactory,
    lifetime: IHostApplicationLifetime,
    registration: ProjectionsRegistration,
    statuses: ProjectionStatuses,
    retries: ProjectionRetries) =
    inherit BackgroundService()

    let mutable Stopping = 0

    /// The status of the custom JS projections KurrentDB links the custom projections' streams with
    static let CustomProjectionsName = "KurrentDB custom projections"

    /// As ProjectionsFactory names it: the type name without "Projection"
    static let NameOf (projectionType: Type) = projectionType.Name.Replace("Projection", String.Empty)

    static let StreamOf (projectionType: Type) =
        match box (projectionType.GetCustomAttribute<HandlesStream>()) with
        | :? HandlesStream as attribute -> attribute.Name
        | _ -> String.Empty

    static let rec (|UnknownEvent|_|) (ex: exn) =
        match ex with
        | :? UnknownEventException as unknown -> Some unknown
        | :? AggregateException as ae when not (isNull ae.InnerException) -> (|UnknownEvent|_|) ae.InnerException
        | _ -> None

    /// A startup error that's the same on every attempt: a mistake in the code or the configuration (MongoDB without a
    /// replica set, a projection without [<HandlesStream>]), not an outage
    static let rec CantHeal (ex: exn) =
        match ex with
        | :? ObjectDisposedException -> false
        | :? InvalidOperationException | :? ArgumentException | :? NotSupportedException -> true
        | :? TargetInvocationException as invocation when not (isNull invocation.InnerException) -> CantHeal invocation.InnerException
        | :? AggregateException as ae when ae.InnerExceptions.Count = 1 -> CantHeal ae.InnerException
        | _ -> false

    new(logger: ILogger<KurrentDBProjectionsHostedServiceInstance>, projectionsFactory: IProjectionsFactory,
        jsProjectionsFactory: IKurrentDBCustomJSProjectionsFactory, lifetime: IHostApplicationLifetime, registration: ProjectionsRegistration) =
        new KurrentDBProjectionsHostedServiceInstance(
            logger, projectionsFactory, jsProjectionsFactory, lifetime, registration, ProjectionStatuses(), ProjectionRetries.Default)

    new(logger: ILogger<KurrentDBProjectionsHostedServiceInstance>, projectionsFactory: IProjectionsFactory,
        jsProjectionsFactory: IKurrentDBCustomJSProjectionsFactory, lifetime: IHostApplicationLifetime, assemblyContainingProjections: Assembly) =
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

    /// With StopApplicationOnFailure: logs it Critical, sets Environment.ExitCode to 1 and stops the application, once.
    /// A failed BackgroundService only stops the host, and the process would exit with code 0
    member private this.StopApplication(ex: exn, reason: string) =
        if Interlocked.Exchange(&Stopping, 1) = 0 then
            logger.LogCritical(ex, "{Reason} Stopping the application (StopApplicationOnFailure).", reason)
            Environment.ExitCode <- 1
            lifetime.StopApplication()

    /// Waits before retry number failures; false when the application stops meanwhile
    member private this.WaitToRetry(failures: int, stoppingToken: CancellationToken) =
        task {
            try
                do! Task.Delay(retries.Delay failures, stoppingToken).ConfigureAwait(false)
                return true
            with :? OperationCanceledException ->
                return false
        }

    /// A startup failure of status: retried, Faulted from FailuresBeforeFaulted on, or Stopped when it can't heal.
    /// Answers whether to retry
    member private this.StartFailed(status: ProjectionStatus, ex: exn, failures: int, stoppingToken: CancellationToken) =
        task {
            if CantHeal ex then
                status.Stopped ex.Message
                RosettaTelemetry.ProjectionFaults.Add(
                    1L, RosettaTelemetry.Tag "rosetta.projection" status.Name, RosettaTelemetry.Tag "rosetta.state" "stopped")
                logger.LogCritical(ex, "Projection {Name} can't start, and STOPPED: retrying won't change this error. The other projections keep running.",
                    status.Name)
                return false
            else
                let state = status.Failed(failures, ex.Message, retries.FailuresBeforeFaulted)
                if failures = retries.FailuresBeforeFaulted then
                    RosettaTelemetry.ProjectionFaults.Add(
                        1L, RosettaTelemetry.Tag "rosetta.projection" status.Name, RosettaTelemetry.Tag "rosetta.state" "faulted")
                    logger.LogCritical(ex, "Projection {Name} is FAULTED: it failed to start {Failures} times in a row. It keeps retrying, every {MaxDelay} at most.",
                        status.Name, failures, retries.MaxDelay)
                elif state = ProjectionState.Retrying then
                    logger.LogError(ex, "Projection {Name} FAILED to start. Retrying in {Delay} (failure {Failures} in a row).",
                        status.Name, retries.Delay failures, failures)
                return! this.WaitToRetry(failures, stoppingToken)
        }

    /// Makes the custom JS projections in KurrentDB, retrying until it can. The projections start after them, as their
    /// cp- streams come from them
    member private this.CreateCustomProjections(stoppingToken: CancellationToken) =
        task {
            let customProjections = this.CustomProjections
            if customProjections.Length > 0 then
                let status = statuses.Of(CustomProjectionsName, String.Join(", ", customProjections |> Seq.map (fun p -> p.ProjectionName)))
                let mutable failures = 0
                let mutable trying = true
                while trying && not stoppingToken.IsCancellationRequested do
                    try
                        do! jsProjectionsFactory.CreateCustomProjections(customProjections).ConfigureAwait(false)
                        status.Progressed 0UL |> ignore
                        trying <- false
                    with ex when not stoppingToken.IsCancellationRequested ->
                        failures <- failures + 1
                        let! retry = this.StartFailed(status, ex, failures, stoppingToken)
                        trying <- retry
        }

    /// Creates and runs the projection until the application stops, or the projection stops for good
    member private this.RunProjection(projectionType: Type, stoppingToken: CancellationToken) : Task =
        task {
            let status = statuses.Of(NameOf projectionType, StreamOf projectionType)
            let mutable failures = 0
            let mutable running = true
            while running && not stoppingToken.IsCancellationRequested do
                let! outcome =
                    task {
                        try
                            let! projection =
                                match registration.NamedProjections |> Seq.tryFind (fun named -> named.ProjectionType = projectionType) with
                                | Some named -> named.Create projectionsFactory
                                | None -> projectionsFactory.CreateAsync projectionType
                            logger.LogInformation("Starting projection {Name} on stream {StreamName}.", projection.Name, projection.SubscriptionStreamName)
                            status.Started projection.Checkpoint.Value
                            // Runs until the application stops: the subscription retries its own failures
                            do! projection.StartAsync(stoppingToken).ConfigureAwait(false)
                            return None
                        with ex ->
                            return Some ex
                    }
                match outcome with
                | None -> running <- false
                | Some _ when stoppingToken.IsCancellationRequested -> running <- false
                // The subscription stopped on it, logged it and marked the projection Stopped
                | Some (UnknownEvent _) -> running <- false
                | Some ex ->
                    failures <- failures + 1
                    let! retry = this.StartFailed(status, ex, failures, stoppingToken)
                    running <- retry
        } :> Task

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            use _stopOnFailure =
                if registration.StopsApplicationOnFailure then
                    statuses.StateChanged.Subscribe(fun status ->
                        match status.State with
                        | ProjectionState.Faulted
                        | ProjectionState.Stopped ->
                            this.StopApplication(null, sprintf "Projection %s is %A: %s" status.Name status.State status.LastError)
                        | _ -> ())
                else
                    null
            try
                do! this.CreateCustomProjections(stoppingToken).ConfigureAwait(false)
                let projectionsToRun =
                    registration.ProjectionTypes
                    |> Seq.map (fun projectionType -> this.RunProjection(projectionType, stoppingToken))
                    |> Seq.toArray
                do! Task.WhenAll(projectionsToRun).ConfigureAwait(false)
            with
            | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> ()
            | ex ->
                // Not a projection's failure (those are each handled where they happen): something unexpected
                logger.LogCritical(ex, "Projections FAILED.")
                if registration.StopsApplicationOnFailure then
                    this.StopApplication(ex, "Projections FAILED.")
        }
