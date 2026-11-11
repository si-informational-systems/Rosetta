namespace SI.Rosetta.Projections.KurrentDB

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics.Metrics
open SI.Rosetta.Common

/// How a projection is doing
[<RequireQualifiedAccess>]
type ProjectionState =
    /// Being created, or its custom projections being made
    | Starting
    /// Subscribed and making progress
    | Running
    /// Failed, and retrying, without having failed FailuresBeforeFaulted times in a row yet
    | Retrying
    /// Failed FailuresBeforeFaulted times in a row without progress (on the same event, or to start): still retrying,
    /// every MaxDelay
    | Faulted
    /// Gave up: an event it has no case for, or a startup error retrying can't heal. Only a restart starts it again
    | Stopped

/// When a failed projection retries, and when it counts as faulted
type ProjectionRetries =
    { /// The wait before the first retry, doubled for each one after it
      InitialDelay: TimeSpan
      /// The longest wait between retries
      MaxDelay: TimeSpan
      /// Failures in a row, without progress, from which the projection is Faulted
      FailuresBeforeFaulted: int }

    /// 1, 2, 4, 8, 16, then 30 s each; Faulted from the 5th failure in a row
    static member Default =
        { InitialDelay = TimeSpan.FromSeconds 1.0
          MaxDelay = TimeSpan.FromSeconds 30.0
          FailuresBeforeFaulted = 5 }

    /// The wait after failure number attempt (1 for the first)
    member this.Delay(attempt: int) =
        let doublings = min (max (attempt - 1) 0) 30
        let ticks = this.InitialDelay.Ticks * (1L <<< doublings)
        if ticks <= 0L || ticks > this.MaxDelay.Ticks then this.MaxDelay else TimeSpan.FromTicks ticks

/// One projection's status, as its subscription and the hosted service report it (Started, Progressed, Failed, Stopped)
[<Sealed; AllowNullLiteral>]
type ProjectionStatus internal (name: string, stream: string, changed: ProjectionStatus -> unit) as this =
    let Lock = obj()
    let mutable CurrentState = ProjectionState.Starting
    let mutable LastCheckpoint = 0UL
    let mutable Failures = 0
    let mutable LastFailure: string = null
    let mutable ChangedAt = DateTimeOffset.UtcNow

    /// Runs update under the lock, then tells the registry when the state changed
    let Update (update: unit -> 'T) =
        let before, result, after =
            lock Lock (fun () ->
                let before = CurrentState
                let result = update ()
                if CurrentState <> before then ChangedAt <- DateTimeOffset.UtcNow
                before, result, CurrentState)
        if after <> before then changed this
        result

    member this.Name = name
    member this.Stream = stream
    member this.State = lock Lock (fun () -> CurrentState)
    /// The checkpoint it started from, or the last event it handled (one-based)
    member this.Checkpoint = lock Lock (fun () -> LastCheckpoint)
    /// Failures in a row since the last progress
    member this.ConsecutiveFailures = lock Lock (fun () -> Failures)
    /// The last failure's message, or null
    member this.LastError = lock Lock (fun () -> LastFailure)
    /// When it went into its State
    member this.Since = lock Lock (fun () -> ChangedAt)

    /// Subscribed from the checkpoint. A retrying projection stays Retrying (or Faulted) until it makes progress
    member this.Started(checkpoint: uint64) =
        Update (fun () ->
            LastCheckpoint <- max LastCheckpoint checkpoint
            if CurrentState = ProjectionState.Starting then CurrentState <- ProjectionState.Running)

    /// A handled event, or caught up. Answers the state it was in before
    member this.Progressed(checkpoint: uint64) =
        Update (fun () ->
            let before = CurrentState
            LastCheckpoint <- max LastCheckpoint checkpoint
            Failures <- 0
            CurrentState <- ProjectionState.Running
            before)

    /// Failure number failures in a row: Faulted from faultedFrom on, Retrying before. Answers the new state
    member this.Failed(failures: int, error: string, faultedFrom: int) =
        Update (fun () ->
            Failures <- failures
            LastFailure <- error
            CurrentState <- if failures >= faultedFrom then ProjectionState.Faulted else ProjectionState.Retrying
            CurrentState)

    member this.Stopped(error: string) =
        Update (fun () ->
            LastFailure <- error
            CurrentState <- ProjectionState.Stopped)

/// Every projection's status in this service, by name: what the "rosetta-projections" health check and the
/// rosetta.projections.unhealthy gauge report
[<Sealed>]
type ProjectionStatuses() as this =
    // Every ProjectionStatuses of the process, for the gauge: weakly, so a disposed host's are let go
    static let Live = List<WeakReference<ProjectionStatuses>>()

    static let Measure () : IEnumerable<Measurement<int>> =
        let alive =
            lock Live (fun () ->
                Live.RemoveAll(fun reference -> not (fst (reference.TryGetTarget()))) |> ignore
                Live |> Seq.choose (fun reference -> match reference.TryGetTarget() with | true, statuses -> Some statuses | _ -> None) |> Seq.toArray)
        [| for statuses in alive do
               for status in statuses.All do
                   let state = status.State
                   let unhealthy = state = ProjectionState.Faulted || state = ProjectionState.Stopped
                   yield Measurement<int>(
                       (if unhealthy then 1 else 0),
                       KeyValuePair("rosetta.projection", box status.Name),
                       KeyValuePair("rosetta.state", box ((string state).ToLowerInvariant()))) |]
        :> IEnumerable<_>

    /// 1 for each projection that's Faulted or Stopped, 0 for the others, by projection and state: alert on its sum > 0
    static let Unhealthy =
        RosettaTelemetry.Meter.CreateObservableGauge<int>(
            "rosetta.projections.unhealthy", Func<IEnumerable<Measurement<int>>>(Measure), "{projection}",
            "Projections that are faulted or stopped (1), and the others (0)")

    let Statuses = ConcurrentDictionary<string, ProjectionStatus>(StringComparer.Ordinal)
    let StateChangedEvent = Event<ProjectionStatus>()

    do
        Unhealthy |> ignore
        lock Live (fun () -> Live.Add(WeakReference<ProjectionStatuses>(this)))

    /// The projection's status, added (Starting) the first time
    member this.Of(name: string, stream: string) =
        Statuses.GetOrAdd(name, fun name -> ProjectionStatus(name, stream, StateChangedEvent.Trigger))

    member this.TryFind(name: string) =
        match Statuses.TryGetValue name with
        | true, status -> Some status
        | false, _ -> None

    /// Every projection's status, by name
    member this.All : IReadOnlyList<ProjectionStatus> =
        Statuses.Values |> Seq.sortBy (fun status -> status.Name) |> Seq.toArray :> IReadOnlyList<_>

    /// A projection went into another state
    [<CLIEvent>]
    member this.StateChanged = StateChangedEvent.Publish
