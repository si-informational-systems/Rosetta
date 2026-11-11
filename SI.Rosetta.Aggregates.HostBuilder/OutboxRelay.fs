namespace SI.Rosetta.Aggregates.HostBuilder

open System
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

/// Publishes the outbox's events with the app's IOutboxPublisher, until the application stops: reads the unpublished
/// ones, publishes them one at a time in the order they were recorded, and marks them published. One instance of the
/// app publishes at a time, the one holding the outbox's lease, which it renews before every batch; the others wait
/// to take over. A failure is retried with a growing delay, from the event that failed: delivery is at least once
type OutboxRelay(outbox: IOutbox, publisher: IOutboxPublisher, options: OutboxOptions, logger: ILogger<OutboxRelay>) =
    inherit BackgroundService()

    /// This instance, as the lease names its owner
    let Owner = sprintf "%s/%O" Environment.MachineName (Guid.NewGuid())
    // 1, 2, 4, 8, 16, then 30 s
    let Delays = [| 1.0; 2.0; 4.0; 8.0; 16.0; 30.0 |] |> Array.map TimeSpan.FromSeconds
    let FailuresBeforeCritical = 5
    /// Whether this instance holds the lease, as far as it knows
    let mutable Leading = false

    let Store =
        match outbox with
        | :? IOutboxRelayStore as store -> store
        | _ ->
            raise (InvalidOperationException(
                sprintf "The outbox (%s) can't be published by UseOutboxPublisher: it doesn't implement IOutboxRelayStore. Register Rosetta's outbox with UseOutboxWith, or publish yours yourself." (outbox.GetType().Name)))

    /// The trace of the command that recorded the event, continued
    static member private Publishing(event: OutboxEvent) =
        let parent =
            match event.TraceParent with
            | null -> ActivityContext()
            | traceParent ->
                match ActivityContext.TryParse(traceParent, event.TraceState) with
                | true, context -> context
                | false, _ -> ActivityContext()
        let activity = RosettaTelemetry.ActivitySource.StartActivity(sprintf "%s %s publish" event.Aggregate event.EventType, ActivityKind.Producer, parent)
        if not (isNull activity) then
            activity
                .SetTag("rosetta.aggregate", event.Aggregate)
                .SetTag("rosetta.aggregate.id", event.AggregateId)
                .SetTag("rosetta.event", event.EventType)
                .SetTag("rosetta.outbox.event.id", event.Id) |> ignore
        activity

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
                    try
                        do! publisher.PublishAsync(event, stoppingToken).ConfigureAwait(false)
                        published.Add event
                        RosettaTelemetry.OutboxPublished.Add(1L, RosettaTelemetry.Tag "rosetta.aggregate" event.Aggregate)
                    with ex ->
                        if not (isNull activity) then activity.SetStatus(ActivityStatusCode.Error, ex.Message).AddException(ex) |> ignore
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
                            if failures = FailuresBeforeCritical then
                                logger.LogCritical(ex, "The outbox {Collection} FAILED to publish {Failures} times in a row. It keeps retrying, every {Delay} at most.",
                                    options.CollectionName, failures, Delays.[Delays.Length - 1])
                            else
                                logger.LogError(ex, "The outbox {Collection} FAILED to publish. Retrying in {Delay} (failure {Failures} in a row).",
                                    options.CollectionName, delay, failures)
                            return delay
                    }
                if wait > TimeSpan.Zero && not stoppingToken.IsCancellationRequested then
                    try
                        do! Task.Delay(wait, stoppingToken).ConfigureAwait(false)
                    with :? OperationCanceledException -> ()
        }
