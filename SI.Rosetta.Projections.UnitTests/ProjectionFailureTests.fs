namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open SI.Rosetta.Common
open SI.Rosetta.Projections
open SI.Rosetta.Projections.HostBuilder
open SI.Rosetta.Projections.KurrentDB

/// A second projection, so a failing one can be seen not to stop the others. It has no handlers
[<HandlesStream("$ce-Ledgers")>]
type LedgerProjection() =
    inherit Projection<OrganizationEvents>()

type ProjectionRetriesTests() =
    [<Fact>]
    member this.``Retries wait 1, 2, 4, 8, 16 s, then 30 s each``() =
        let delays = [ for attempt in 1 .. 8 -> ProjectionRetries.Default.Delay(attempt).TotalSeconds ]
        Assert.Equal<float list>([ 1.0; 2.0; 4.0; 8.0; 16.0; 30.0; 30.0; 30.0 ], delays)

    [<Fact>]
    member this.``A projection is Faulted from its 5th failure in a row``() =
        Assert.Equal(5, ProjectionRetries.Default.FailuresBeforeFaulted)
        // However many failures: no overflow
        Assert.Equal(TimeSpan.FromSeconds 30.0, ProjectionRetries.Default.Delay 1000)

type ProjectionStatusTests() =
    let Statuses = ProjectionStatuses()

    [<Fact>]
    member this.``A projection runs once it's started, and retries, faults and recovers with its failures``() =
        let changes = List<ProjectionState>()
        use _ = Statuses.StateChanged.Subscribe(fun status -> changes.Add status.State)
        let status = Statuses.Of("Organization", "$ce-Organizations")
        Assert.Equal(ProjectionState.Starting, status.State)

        status.Started 3UL
        let afterFirst = status.Failed(1, "unavailable", 2)
        let afterSecond = status.Failed(2, "unavailable", 2)
        let before = status.Progressed 4UL

        Assert.Equal(ProjectionState.Retrying, afterFirst)
        Assert.Equal(ProjectionState.Faulted, afterSecond)
        Assert.Equal(ProjectionState.Faulted, before)
        Assert.Equal(ProjectionState.Running, status.State)
        Assert.Equal(4UL, status.Checkpoint)
        Assert.Equal(0, status.ConsecutiveFailures)
        Assert.Equal("unavailable", status.LastError)
        Assert.Equal<ProjectionState list>(
            [ ProjectionState.Running; ProjectionState.Retrying; ProjectionState.Faulted; ProjectionState.Running ], List.ofSeq changes)

    [<Fact>]
    member this.``A retrying projection that subscribes again is still Retrying until it makes progress``() =
        let status = Statuses.Of("Organization", "$ce-Organizations")
        status.Started 0UL
        status.Failed(1, "dropped", 5) |> ignore

        status.Started 0UL

        Assert.Equal(ProjectionState.Retrying, status.State)

    [<Fact>]
    member this.``Each projection has one status``() =
        Assert.Same(Statuses.Of("Organization", "$ce-Organizations"), Statuses.Of("Organization", "$ce-Organizations"))

/// The rosetta.projections.unhealthy gauge, read with a MeterListener
type ProjectionsUnhealthyGaugeTests() =
    [<Fact>]
    member this.``The gauge reports 1 for a faulted or stopped projection and 0 for a running one``() =
        let statuses = ProjectionStatuses()
        let suffix = Guid.NewGuid().ToString("N")
        statuses.Of("Faulted-" + suffix, "$ce-A").Failed(5, "boom", 5) |> ignore
        statuses.Of("Stopped-" + suffix, "$ce-B").Stopped("unknown event")
        statuses.Of("Running-" + suffix, "$ce-C").Started(1UL)
        let measured = ConcurrentDictionary<string, int * string>()
        use listener = new System.Diagnostics.Metrics.MeterListener()
        listener.InstrumentPublished <- fun instrument listener ->
            if instrument.Meter.Name = RosettaTelemetry.Name && instrument.Name = "rosetta.projections.unhealthy" then
                listener.EnableMeasurementEvents(instrument)
        listener.SetMeasurementEventCallback<int>(fun _ value tags _ ->
            let tagged = tags.ToArray()
            let tag name = tagged |> Array.find (fun tag -> tag.Key = name) |> fun tag -> string tag.Value
            let projection = tag "rosetta.projection"
            if projection.EndsWith suffix then
                measured.[projection] <- (value, tag "rosetta.state"))
        listener.Start()

        listener.RecordObservableInstruments()

        Assert.Equal((1, "faulted"), measured.["Faulted-" + suffix])
        Assert.Equal((1, "stopped"), measured.["Stopped-" + suffix])
        Assert.Equal((0, "running"), measured.["Running-" + suffix])

type ProjectionsHealthCheckTests() =
    let Statuses = ProjectionStatuses()
    let HealthCheck = ProjectionsHealthCheck(Statuses)

    [<Fact>]
    member this.``Healthy while every projection runs``() =
        Statuses.Of("Organization", "$ce-Organizations").Started 0UL
        Statuses.Of("Ledger", "$ce-Ledgers").Started 0UL

        let result = HealthCheck.Check HealthStatus.Unhealthy

        Assert.Equal(HealthStatus.Healthy, result.Status)
        Assert.Equal("2 projections running.", result.Description)

    [<Fact>]
    member this.``Degraded while one retries``() =
        Statuses.Of("Organization", "$ce-Organizations").Started 0UL
        Statuses.Of("Ledger", "$ce-Ledgers").Failed(1, "dropped", 5) |> ignore

        let result = HealthCheck.Check HealthStatus.Unhealthy

        Assert.Equal(HealthStatus.Degraded, result.Status)
        Assert.Equal("2 projections: Ledger retrying.", result.Description)

    [<Fact>]
    member this.``The registration's failure status when one is faulted or stopped, with each projection in Data``() =
        Statuses.Of("Organization", "$ce-Organizations").Failed(5, "handler failure", 5) |> ignore
        Statuses.Of("Ledger", "$ce-Ledgers").Stopped "unknown event"

        let result = HealthCheck.Check HealthStatus.Unhealthy

        Assert.Equal(HealthStatus.Unhealthy, result.Status)
        let organization = result.Data.["Organization"] :?> IDictionary<string, obj>
        Assert.Equal(box "Faulted", organization.["state"])
        Assert.Equal(box "handler failure", organization.["lastError"])
        Assert.Equal(box 5, organization.["consecutiveFailures"])
        Assert.Equal(box "$ce-Organizations", organization.["stream"])
        Assert.Equal(HealthStatus.Degraded, (HealthCheck.Check HealthStatus.Degraded).Status)

    [<Fact>]
    member this.``Healthy before any projection starts``() =
        Assert.Equal(HealthStatus.Healthy, (HealthCheck.Check HealthStatus.Unhealthy).Status)

    [<Fact>]
    member this.``AddRosettaProjections registers the rosetta-projections check``() =
        let services = ServiceCollection()
        services.AddSingleton(Statuses).AddHealthChecks().AddRosettaProjections() |> ignore
        use provider = services.BuildServiceProvider()

        let options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()

        Assert.Contains(options.Value.Registrations, fun registration -> registration.Name = "rosetta-projections")

/// The hosted service, with projections that fail as told
type ProjectionsHostedServiceTests() =
    let Statuses = ProjectionStatuses()
    let Fast = { InitialDelay = TimeSpan.FromMilliseconds 1.0; MaxDelay = TimeSpan.FromMilliseconds 5.0; FailuresBeforeFaulted = 3 }
    let Started = ConcurrentBag<string>()
    let StopRequested = ref 0

    let Lifetime =
        { new IHostApplicationLifetime with
            member _.ApplicationStarted = CancellationToken.None
            member _.ApplicationStopping = CancellationToken.None
            member _.ApplicationStopped = CancellationToken.None
            member _.StopApplication() = Interlocked.Increment(&StopRequested.contents) |> ignore }

    let NoCustomProjections =
        { new IKurrentDBCustomJSProjectionsFactory with
            member _.CreateCustomProjections(_: Reflection.Assembly) = Task.CompletedTask
            member _.CreateCustomProjections(_: KurrentDBProjectionParameters seq) = Task.CompletedTask }

    /// Runs until canceled, or fails as start says
    let Projection (name: string) (start: CancellationToken -> Task) =
        let checkpoint = ref (Checkpoint(Id = "Checkpoints-" + name))
        { new IProjection with
            member _.Name with get () = name and set _ = ()
            member _.SubscriptionStreamName with get () = "" and set _ = ()
            member _.Checkpoint with get () = checkpoint.Value and set value = checkpoint.Value <- value
            member _.StartAsync(cancellationToken) =
                Started.Add name
                start cancellationToken }

    let UntilCanceled (cancellationToken: CancellationToken) =
        task {
            try
                do! Task.Delay(Timeout.Infinite, cancellationToken)
            with :? OperationCanceledException -> ()
        } :> Task

    /// creates answers each projection type's next creation
    let Factory (create: Type -> Task<IProjection>) =
        { new IProjectionsFactory with
            member _.CreateFromAssemblyAsync(_) = failwith "not used"
            member _.CreateAsync(t: Type) = create t
            member _.CreateAsync<'TEvent when 'TEvent :> IAggregateEvents>(t: Type) = create t }

    let Registration (stopApplicationOnFailure: bool) =
        let captured = ref Unchecked.defaultof<ProjectionsRegistration>
        let configuration =
            ConfigurationBuilder()
                .AddInMemoryCollection(dict [
                    "KurrentDB:ConnectionString", "kurrentdb://localhost:2113?tls=false"
                    "MongoDB:Host", "localhost"
                    "MongoDB:DatabaseName", "Rosetta-ProjectionFailureTests" ])
                .Build()
        ServiceCollection().AddProjectionsWith<KurrentDB, MongoDB>(configuration, fun projections ->
            projections.Projection<OrganizationProjection, OrganizationEvents>()
            projections.Projection<LedgerProjection, OrganizationEvents>()
            if stopApplicationOnFailure then projections.StopApplicationOnFailure()
            captured.Value <- projections) |> ignore
        captured.Value

    let Service (registration: ProjectionsRegistration) (factory: IProjectionsFactory) =
        new KurrentDBProjectionsHostedServiceInstance(
            NullLogger<KurrentDBProjectionsHostedServiceInstance>.Instance, factory, NoCustomProjections, Lifetime,
            registration, Statuses, Fast)

    let Eventually (what: string) (condition: unit -> bool) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 10.0
            while not (condition ()) && DateTime.UtcNow < deadline do
                do! Task.Delay 10
            Assert.True(condition (), what)
        }

    let StateOf name = (Statuses.Of(name, "")).State

    /// LedgerProjection always starts and runs; OrganizationProjection's creation as fail says
    let RunOrganizationFailing (stopApplicationOnFailure: bool) (fail: int -> exn option) =
        let attempts = ref 0
        let factory =
            Factory (fun t ->
                if t = typeof<OrganizationProjection> then
                    attempts.Value <- attempts.Value + 1
                    match fail attempts.Value with
                    | Some ex -> Task.FromException<IProjection> ex
                    | None -> Task.FromResult(Projection "Organization" UntilCanceled)
                else
                    Task.FromResult(Projection "Ledger" UntilCanceled))
        Service (Registration stopApplicationOnFailure) factory, attempts

    [<Fact>]
    member this.``A projection that can't be created during an outage is retried until it can, and faults meanwhile``() =
        task {
            let service, attempts = RunOrganizationFailing false (fun attempt -> if attempt <= 4 then Some (TimeoutException "KurrentDB unavailable") else None)
            use cancellation = new CancellationTokenSource()

            do! service.StartAsync(cancellation.Token)
            do! Eventually "Organization is started on its 5th attempt" (fun () -> Seq.contains "Organization" Started)

            Assert.Equal(5, attempts.Value)
            Assert.Contains("Ledger", Started)
            // Faulted from the 3rd failure: running again once its subscription makes progress
            Assert.Equal(ProjectionState.Faulted, StateOf "Organization")
            Assert.Equal(0, StopRequested.Value)
            cancellation.Cancel()
            do! service.StopAsync(CancellationToken.None)
        }

    [<Fact>]
    member this.``A projection that can't start for a reason retrying won't change stops alone``() =
        task {
            let service, attempts = RunOrganizationFailing false (fun _ -> Some (InvalidOperationException "MongoDB projections need a replica set"))
            use cancellation = new CancellationTokenSource()

            do! service.StartAsync(cancellation.Token)
            do! Eventually "Organization is stopped" (fun () -> StateOf "Organization" = ProjectionState.Stopped)
            do! Eventually "Ledger is started" (fun () -> Seq.contains "Ledger" Started)

            Assert.Equal(1, attempts.Value)
            Assert.Equal("MongoDB projections need a replica set", (Statuses.Of("Organization", "")).LastError)
            Assert.Equal(0, StopRequested.Value)
            cancellation.Cancel()
            do! service.StopAsync(CancellationToken.None)
        }

    [<Fact>]
    member this.``A projection stopped by an unknown event leaves the others running``() =
        task {
            let unknown = UnknownEventException("ParcelLost", typeof<OrganizationEvents>, "$ce-Organizations", 7UL)
            let factory =
                Factory (fun t ->
                    if t = typeof<OrganizationProjection> then
                        Task.FromResult(Projection "Organization" (fun _ -> Task.FromException unknown))
                    else
                        Task.FromResult(Projection "Ledger" UntilCanceled))
            let service = Service (Registration false) factory
            use cancellation = new CancellationTokenSource()

            do! service.StartAsync(cancellation.Token)
            do! Eventually "both are started" (fun () -> Seq.contains "Organization" Started && Seq.contains "Ledger" Started)
            do! Task.Delay 50

            // Started once: not retried
            Assert.Equal(1, Started |> Seq.filter ((=) "Organization") |> Seq.length)
            Assert.Equal(0, StopRequested.Value)
            Assert.False(service.ExecuteTask.IsCompleted, "the service stopped")
            cancellation.Cancel()
            do! service.StopAsync(CancellationToken.None)
        }

    [<Fact>]
    member this.``StopApplicationOnFailure stops the application when a projection can't start``() =
        task {
            let service, _ = RunOrganizationFailing true (fun _ -> Some (InvalidOperationException "MongoDB projections need a replica set"))
            use cancellation = new CancellationTokenSource()
            try
                do! service.StartAsync(cancellation.Token)
                do! Eventually "the application is stopped" (fun () -> StopRequested.Value = 1)

                Assert.Equal(1, Environment.ExitCode)
            finally
                Environment.ExitCode <- 0
            cancellation.Cancel()
            do! service.StopAsync(CancellationToken.None)
        }

    [<Fact>]
    member this.``StopApplicationOnFailure stops the application when a projection faults``() =
        task {
            let service, _ = RunOrganizationFailing true (fun _ -> Some (TimeoutException "KurrentDB unavailable"))
            use cancellation = new CancellationTokenSource()
            try
                do! service.StartAsync(cancellation.Token)
                do! Eventually "the application is stopped" (fun () -> StopRequested.Value = 1)

                Assert.Equal(ProjectionState.Faulted, StateOf "Organization")
            finally
                Environment.ExitCode <- 0
            cancellation.Cancel()
            do! service.StopAsync(CancellationToken.None)
        }
