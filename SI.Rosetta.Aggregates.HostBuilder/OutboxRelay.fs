namespace SI.Rosetta.Aggregates.HostBuilder

open System
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Reflection
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

/// An outbox event the relay can't read back as its events union: it's held, like a failed publication
type internal UndecodableOutboxEventException(event: OutboxEvent, reason: string, inner: exn) =
    inherit Exception(
        sprintf "Cannot decode %s as %s: %s." event.EventType
            (if String.IsNullOrEmpty event.EventsUnionClrType then "(no events union)" else event.EventsUnionClrType) reason,
        inner)
    member this.Event = event
    member this.Reason = reason

/// Publishes the outbox's events with the app's IOutboxPublisher, until the application stops: reads the unpublished
/// ones, decodes each into its events union (through the upcasters), publishes them one at a time in the order they
/// were recorded, and marks them published. One instance of the app publishes at a time, the one holding the outbox's
/// lease, which it renews before every batch; the others wait to take over. A failure, or an event that can't be
/// decoded, is retried with a growing delay, from the event that failed: delivery is at least once, in order, and no
/// event is skipped
type OutboxRelay(outbox: IOutbox, publisher: IOutboxPublisher, upcasters: IEventUpcaster seq, options: OutboxOptions, logger: ILogger<OutboxRelay>) =
    inherit BackgroundService()

    /// This instance, as the lease names its owner
    let Owner = sprintf "%s/%O" Environment.MachineName (Guid.NewGuid())
    let Upcasters = Seq.toArray upcasters
    // 1, 2, 4, 8, 16, then 30 s
    let Delays = [| 1.0; 2.0; 4.0; 8.0; 16.0; 30.0 |] |> Array.map TimeSpan.FromSeconds
    let FailuresBeforeCritical = 5
    /// Whether this instance holds the lease, as far as it knows
    let mutable Leading = false
    /// The event whose publication last threw, and how many times in a row it did
    let mutable FailedEventId = Guid.Empty
    let mutable FailedAttempts = 0

    let Store =
        match outbox with
        | :? IOutboxRelayStore as store -> store
        | _ ->
            raise (InvalidOperationException(
                sprintf "The outbox (%s) can't be published by UseOutboxPublisher: it doesn't implement IOutboxRelayStore. Register Rosetta's outbox with UseOutboxWith, or publish yours yourself." (outbox.GetType().Name)))

    /// Without upcasters: the events are read as they were recorded
    new(outbox: IOutbox, publisher: IOutboxPublisher, options: OutboxOptions, logger: ILogger<OutboxRelay>) =
        new OutboxRelay(outbox, publisher, Seq.empty, options, logger)

    /// The trace of the command that recorded the event, or none
    static member private CommandTraceOf(event: OutboxEvent) =
        match event.TraceParent with
        | null -> ActivityContext()
        | traceParent ->
            match ActivityContext.TryParse(traceParent, event.TraceState) with
            | true, context -> context
            | false, _ -> ActivityContext()

    /// The trace of the command that recorded the event, continued
    static member private Publishing(event: OutboxEvent) =
        let activity =
            RosettaTelemetry.ActivitySource.StartActivity(
                sprintf "%s %s publish" event.Aggregate event.EventType, ActivityKind.Producer, OutboxRelay.CommandTraceOf event)
        if not (isNull activity) then
            activity
                .SetTag("rosetta.aggregate", event.Aggregate)
                .SetTag("rosetta.aggregate.id", event.AggregateId)
                .SetTag("rosetta.event", event.EventType)
                .SetTag("rosetta.outbox.event.id", event.Id) |> ignore
        activity

    static member private Counted (event: OutboxEvent) (outcome: string) =
        RosettaTelemetry.OutboxPublished.Add(
            1L, RosettaTelemetry.Tag "rosetta.aggregate" event.Aggregate, RosettaTelemetry.Tag "rosetta.outcome" outcome)

    /// What the publisher is handed: the event decoded through the upcasters, and everything recorded about it
    member private this.PublicationOf(event: OutboxEvent, attempt: int) : Result<OutboxPublication, exn> =
        try
            match OutboxEvents.Decode Upcasters event with
            | Ok decoded ->
                let case, fields = FSharpValue.GetUnionFields(decoded, decoded.GetType())
                Ok { Event = decoded
                     EventRecord = fields.[0]
                     EventRecordType = GetUnionCaseType case
                     EventsUnionType = case.DeclaringType
                     EventTypeName = event.EventType
                     EventJson = event.Data
                     EventId = event.Id
                     AggregateName = event.Aggregate
                     AggregateId = event.AggregateId
                     AggregateVersion = event.Version
                     PositionInCommand = event.Position
                     RecordedAt = event.RecordedAt
                     CommandTrace = OutboxRelay.CommandTraceOf event
                     Attempt = attempt }
            | Error error -> Error (UndecodableOutboxEventException(event, error.Reason, null) :> exn)
        // Its data doesn't fit its record, or an upcaster threw
        with ex -> Error (UndecodableOutboxEventException(event, ex.Message, ex) :> exn)

    /// Publishes the events in order, and marks the published ones, also when one fails. Answers how many it published
    member private this.PublishAsync(events: OutboxEvent[], stoppingToken: CancellationToken) : Task<int> =
        task {
            let published = Collections.Generic.List<OutboxEvent>()
            let mutable failure = None
            let mutable remaining = List.ofArray (OutboxEvents.InPublishingOrder events)
            let renewed = Stopwatch.StartNew()
            while not remaining.IsEmpty do
                // A long batch renews the lease as it goes, and stops once another instance holds it
                let! leased =
                    if renewed.Elapsed < options.PublishLeaseDuration / 3.0 then Task.FromResult true
                    else
                        renewed.Restart()
                        Store.TryLeaseAsync(Owner, options.PublishLeaseDuration, stoppingToken)
                if not leased then
                    Leading <- false
                    remaining <- []
                else
                    let event = remaining.Head
                    remaining <- remaining.Tail
                    use activity = OutboxRelay.Publishing event
                    let attempt = if event.Id = FailedEventId then FailedAttempts + 1 else 1
                    match this.PublicationOf(event, attempt) with
                    | Error undecodable ->
                        if not (isNull activity) then activity.SetStatus(ActivityStatusCode.Error, undecodable.Message) |> ignore
                        OutboxRelay.Counted event "undecodable"
                        failure <- Some (ExceptionDispatchInfo.Capture undecodable)
                        remaining <- []
                    | Ok publication ->
                        if not (isNull activity) then activity.SetTag("rosetta.event.case", (fst (FSharpValue.GetUnionFields(publication.Event, publication.Event.GetType()))).Name) |> ignore
                        try
                            do! publisher.PublishAsync(publication, stoppingToken).ConfigureAwait(false)
                            published.Add event
                            OutboxRelay.Counted event "published"
                        with ex ->
                            if not (isNull activity) then activity.SetStatus(ActivityStatusCode.Error, ex.Message).AddException(ex) |> ignore
                            OutboxRelay.Counted event "failed"
                            FailedEventId <- event.Id
                            FailedAttempts <- attempt
                            failure <- Some (ExceptionDispatchInfo.Capture ex)
                            remaining <- []
            if published.Count > 0 then
                // Not on the stopping token: what was published is marked even while the app stops
                do! Store.MarkPublishedAsync(published.ToArray(), DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false)
            failure |> Option.iter (fun failure -> failure.Throw())
            return published.Count
        }

    member private this.StopRelayAsync(cancellationToken: CancellationToken) = base.StopAsync(cancellationToken)

    /// Hands the lease over when the app stops: another instance takes over at once, not once it expires
    override this.StopAsync(cancellationToken: CancellationToken) =
        task {
            do! this.StopRelayAsync(cancellationToken).ConfigureAwait(false)
            if Leading then
                try
                    let! _ = Store.TryLeaseAsync(Owner, TimeSpan.Zero, cancellationToken).ConfigureAwait(false)
                    Leading <- false
                with ex ->
                    logger.LogWarning(ex, "The outbox {Collection}'s lease couldn't be handed over: another instance takes over once it expires.", options.CollectionName)
        } :> Task

    member private this.LogFailure(ex: exn, failures: int, delay: TimeSpan) =
        match ex with
        | :? UndecodableOutboxEventException as undecodable ->
            let event = undecodable.Event
            if failures = FailuresBeforeCritical then
                logger.LogCritical(
                    undecodable.InnerException,
                    "The outbox {Collection} cannot decode {EventType} as {EventsUnionClrType}: {Reason} ({Failures} times in a row). It and the events after it wait; it keeps retrying, every {Delay} at most.",
                    options.CollectionName, event.EventType, event.EventsUnionClrType, undecodable.Reason, failures, Delays.[Delays.Length - 1])
            else
                logger.LogError(
                    undecodable.InnerException,
                    "The outbox {Collection} cannot decode {EventType} as {EventsUnionClrType}: {Reason}. Retrying in {Delay} (failure {Failures} in a row).",
                    options.CollectionName, event.EventType, event.EventsUnionClrType, undecodable.Reason, delay, failures)
        | _ ->
            if failures = FailuresBeforeCritical then
                logger.LogCritical(ex, "The outbox {Collection} FAILED to publish {Failures} times in a row. It keeps retrying, every {Delay} at most.",
                    options.CollectionName, failures, Delays.[Delays.Length - 1])
            else
                logger.LogError(ex, "The outbox {Collection} FAILED to publish. Retrying in {Delay} (failure {Failures} in a row).",
                    options.CollectionName, delay, failures)

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            let mutable failures = 0
            while not stoppingToken.IsCancellationRequested do
                let! wait =
                    task {
                        try
                            let! leased = Store.TryLeaseAsync(Owner, options.PublishLeaseDuration, stoppingToken).ConfigureAwait(false)
                            if leased <> Leading then
                                Leading <- leased
                                if leased then logger.LogInformation("The outbox {Collection} is published by this instance ({Owner}).", options.CollectionName, Owner)
                                else logger.LogInformation("The outbox {Collection} is published by another instance: this one ({Owner}) waits to take over.", options.CollectionName, Owner)
                            if not leased then
                                return options.PublishLeaseDuration / 2.0
                            else
                                let! events = Store.UnpublishedAsync(options.PublishBatchSize, stoppingToken).ConfigureAwait(false)
                                let! published = this.PublishAsync(events, stoppingToken).ConfigureAwait(false)
                                if failures > 0 then
                                    logger.LogInformation("The outbox {Collection} RECOVERED: it publishes again.", options.CollectionName)
                                    failures <- 0
                                // A full batch: more may be waiting
                                return if published = options.PublishBatchSize then TimeSpan.Zero else options.PublishPollInterval
                        with
                        | :? OperationCanceledException when stoppingToken.IsCancellationRequested -> return TimeSpan.Zero
                        | ex ->
                            failures <- failures + 1
                            let delay = Delays.[min (failures - 1) (Delays.Length - 1)]
                            this.LogFailure(ex, failures, delay)
                            return delay
                    }
                if wait > TimeSpan.Zero && not stoppingToken.IsCancellationRequested then
                    try
                        do! Task.Delay(wait, stoppingToken).ConfigureAwait(false)
                    with :? OperationCanceledException -> ()
        }
