namespace SI.Rosetta.Projections.KurrentDB

open System
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open KurrentDB.Client
open Microsoft.Extensions.Logging
open SI.Rosetta.Projections
open SI.Rosetta.Common
open FSharp.Control
open SI.Rosetta.Serialization

/// A catch-up subscription that never gives up on its own: it resubscribes after any failure, waiting longer each time
/// (ProjectionRetries), and reports how it's doing in ProjectionStatuses. Failing FailuresBeforeFaulted times in a row
/// without progress makes it Faulted (logged Critical once), and it keeps retrying. Only an event 'TEvent has no case
/// for (without SkipUnknownEvents) stops it, since retrying can't help: StartAsync then fails with UnknownEventException
type KurrentDBSubscription<'TEvent when 'TEvent :> IAggregateEvents>
    (logger: ILogger<KurrentDBSubscription<'TEvent>>, client: KurrentDBClient, upcasters: IEventUpcaster seq,
     statuses: ProjectionStatuses, retries: ProjectionRetries) =
    let Upcasters = Seq.toArray upcasters
    let mutable name = String.Empty
    let mutable streamName = String.Empty
    let mutable currentCheckpoint = 0UL
    let mutable resubscriptionAttempt = 0
    let mutable hasFailed = false
    let mutable error = String.Empty
    let mutable eventReceived: 'TEvent * uint64 -> Task<unit> = fun (ev, cp) -> Task.FromResult<unit>()
    let mutable skipUnknownEvents = false
    // Each skipped event type is logged as a warning the first time only
    let SkippedEventTypes = Collections.Generic.HashSet<string>()

    let IsDisposal (ex: exn) =
        match ex with
        | :? ObjectDisposedException -> true
        | :? AggregateException as ae when (ae.InnerException :? ObjectDisposedException) -> true
        | _ -> false

    // Retrying can't make an unknown event known, so the subscription fails at once
    let rec (|UnknownEvent|_|) (ex: exn) =
        match ex with
        | :? UnknownEventException as unknown -> Some unknown
        | :? AggregateException as ae when not (isNull ae.InnerException) -> (|UnknownEvent|_|) ae.InnerException
        | _ -> None

    let isNotDeleted (event: ResolvedEvent) =
        event.Event <> null

    new(logger: ILogger<KurrentDBSubscription<'TEvent>>, client: KurrentDBClient, upcasters: IEventUpcaster seq) =
        KurrentDBSubscription<'TEvent>(logger, client, upcasters, ProjectionStatuses(), ProjectionRetries.Default)

    new(logger: ILogger<KurrentDBSubscription<'TEvent>>, client: KurrentDBClient) =
        KurrentDBSubscription<'TEvent>(logger, client, Seq.empty)

    /// This projection's status (its Name is set after it's created)
    member private this.Status = statuses.Of(name, streamName)

    /// A handled event or catching up: the failures in a row start again from 0
    member private this.Progressed(checkpoint: uint64) =
        resubscriptionAttempt <- 0
        match this.Status.Progressed checkpoint with
        | ProjectionState.Faulted ->
            logger.LogInformation("Projection {Name} on stream {StreamName} RECOVERED: it handles events again, from checkpoint {Checkpoint}.",
                this.Name, this.StreamName, checkpoint)
        | _ -> ()

    member private this.LogSkipped (event: ResolvedEvent) =
        let typeName = event.Event.EventType
        let level = if SkippedEventTypes.Add typeName then LogLevel.Warning else LogLevel.Debug
        logger.Log(level, "Projection {Name} SKIPPED event {EventType} (event {EventNumber} of stream {EventStream}): {EventsType} has no case for it.",
            this.Name, typeName, event.Event.EventNumber.ToUInt64(), event.Event.EventStreamId, typeof<'TEvent>.Name)
        RosettaTelemetry.ProjectionEvents.Add(
            1L, RosettaTelemetry.Tag "rosetta.projection" this.Name, RosettaTelemetry.Tag "rosetta.outcome" "skipped")
        match Activity.Current with
        | null -> ()
        | activity -> activity.SetTag("rosetta.outcome", "skipped") |> ignore

    member private this.HandleEvent (event: ResolvedEvent) =
        task {
            let oneBasedCheckPoint = event.OriginalEventNumber.ToUInt64() + 1UL
            if isNotDeleted event then
                // Continues the trace of the command that stored the event, read only when someone listens
                let parent =
                    if RosettaTelemetry.ActivitySource.HasListeners() then KurrentDBSerialization.TraceContextOf event
                    else ActivityContext()
                use activity =
                    RosettaTelemetry.ActivitySource.StartActivity(
                        sprintf "%s %s" this.Name event.Event.EventType, ActivityKind.Consumer, parent)
                if not (isNull activity) then
                    activity
                        .SetTag("rosetta.projection", this.Name)
                        .SetTag("rosetta.stream", event.Event.EventStreamId)
                        .SetTag("rosetta.event", event.Event.EventType)
                        .SetTag("rosetta.checkpoint", oneBasedCheckPoint) |> ignore
                try
                    if skipUnknownEvents then
                        match KurrentDBSerialization.TryDeserializeWith<'TEvent> Upcasters event with
                        | Some ev -> do! eventReceived(ev, oneBasedCheckPoint).ConfigureAwait(false)
                        | None -> this.LogSkipped event
                    else
                        let ev = KurrentDBSerialization.DeserializeWith<'TEvent> Upcasters event
                        do! eventReceived(ev, oneBasedCheckPoint).ConfigureAwait(false)
                with ex ->
                    if not (isNull activity) then
                        activity.SetStatus(ActivityStatusCode.Error, ex.Message).AddException(ex) |> ignore
                    ExceptionDispatchInfo.Throw ex

            currentCheckpoint <- oneBasedCheckPoint
            this.Progressed oneBasedCheckPoint
        }

    interface ISubscription<'TEvent> with
        member this.Name
            with get() = name
            and set(value) = name <- value
            
        member this.StreamName
            with get() = streamName
            and set(value) = streamName <- value
            
        member this.EventReceived
            with get() = eventReceived
            and set(value) = eventReceived <- value

        member this.SkipUnknownEvents
            with get() = skipUnknownEvents
            and set(value) = skipUnknownEvents <- value

        member this.StartAsync(oneBasedCheckpoint, cancellationToken) =
            task {
                currentCheckpoint <- oneBasedCheckpoint
                let mutable checkpoint =
                    if currentCheckpoint = 0UL then
                        FromStream.Start
                    else
                        let startingCheckpoint = currentCheckpoint - 1UL
                        FromStream.After(startingCheckpoint)
                let mutable stopped = false

                while not stopped do
                    let! dropped =
                        task {
                            try
                                use subscription = client.SubscribeToStream(
                                        this.StreamName,
                                        checkpoint,
                                        resolveLinkTos = true,
                                        cancellationToken = cancellationToken)

                                // AsyncSeq<'T> is IAsyncEnumerable<'T>, so the Messages can be folded over directly
                                let asyncSubscription: AsyncSeq<StreamMessage> = subscription.Messages

                                do! asyncSubscription
                                    |> AsyncSeq.foldAsync (fun _ message ->
                                        async {
                                            match message with
                                            | :? StreamMessage.SubscriptionConfirmation ->
                                                logger.LogInformation(
                                                    "Projection {Name} STARTED on stream {StreamName}.",
                                                    this.Name, this.StreamName)
                                                this.Status.Started currentCheckpoint
                                            // Only progress resets the failures in a row: a handled event (HandleEvent) or catching up.
                                            // Not the confirmation, or a projection failing on the same event would never be Faulted
                                            | :? StreamMessage.CaughtUp ->
                                                this.Progressed currentCheckpoint
                                            | :? StreamMessage.Event as evnt ->
                                                do! this.HandleEvent evnt.ResolvedEvent |> Async.AwaitTask
                                                checkpoint <- FromStream.After(evnt.ResolvedEvent.OriginalEventNumber)
                                            | _ -> ()
                                            return ()
                                        }) ()
                                return None
                            with ex ->
                                return Some ex
                        }

                    match dropped with
                    | None ->
                        stopped <- true
                    | Some _ when cancellationToken.IsCancellationRequested ->
                        logger.LogInformation("Subscription {Name} on stream {StreamName} was CANCELED.", this.Name, this.StreamName)
                        stopped <- true
                    | Some ex when IsDisposal ex ->
                        logger.LogInformation("Subscription {Name} on stream {StreamName} was CANCELED by the user.", this.Name, this.StreamName)
                        stopped <- true
                    | Some (UnknownEvent unknown) ->
                        hasFailed <- true
                        error <- unknown.Message
                        this.Status.Stopped unknown.Message
                        RosettaTelemetry.ProjectionFaults.Add(
                            1L, RosettaTelemetry.Tag "rosetta.projection" this.Name, RosettaTelemetry.Tag "rosetta.state" "stopped")
                        logger.LogCritical(unknown, "Projection {Name} on stream {StreamName} STOPPED on an event it has no case for.",
                            this.Name, this.StreamName)
                        ExceptionDispatchInfo.Capture(unknown).Throw()
                    | Some ex ->
                        resubscriptionAttempt <- resubscriptionAttempt + 1
                        RosettaTelemetry.Resubscriptions.Add(1L, RosettaTelemetry.Tag "rosetta.projection" this.Name)
                        error <- ex.Message
                        this.Status.Failed(resubscriptionAttempt, ex.Message, retries.FailuresBeforeFaulted) |> ignore
                        let delay = retries.Delay resubscriptionAttempt
                        if resubscriptionAttempt = retries.FailuresBeforeFaulted then
                            RosettaTelemetry.ProjectionFaults.Add(
                                1L, RosettaTelemetry.Tag "rosetta.projection" this.Name, RosettaTelemetry.Tag "rosetta.state" "faulted")
                            logger.LogCritical(ex, "Projection {Name} on stream {StreamName} is FAULTED: it failed {Failures} times in a row on the event after checkpoint {Checkpoint}. It keeps retrying, every {MaxDelay} at most.",
                                this.Name, this.StreamName, resubscriptionAttempt, currentCheckpoint, retries.MaxDelay)
                        else
                            logger.LogError(ex, "Subscription {Name} on stream {StreamName} DROPPED after checkpoint {Checkpoint}. Resubscribing in {Delay} (failure {Failures} in a row).",
                                this.Name, this.StreamName, currentCheckpoint, delay, resubscriptionAttempt)
                        try
                            do! Task.Delay(delay, cancellationToken).ConfigureAwait(false)
                        with :? OperationCanceledException ->
                            logger.LogInformation("Subscription {Name} on stream {StreamName} was CANCELED.", this.Name, this.StreamName)
                            stopped <- true
            }

    member this.Name
        with get() = name
        and set value = name <- value
    member this.StreamName
        with get() = streamName
        and set value = streamName <- value
    member this.HasFailed = hasFailed
    member this.Error = error
    member this.EventReceived 
        with get() = eventReceived
        and set(value) = eventReceived<- value

    /// Only the custom projections' cp- streams, which Rosetta deletes and links again: a checkpoint at or before the
    /// stream's truncation was reached in the links that were deleted, and one past the stream's end in another stream
    /// (e.g. the stream a renamed custom projection linked before)
    interface ISubscriptionHistory with
        member this.ReplacedSinceAsync(checkpoint) =
            task {
                if checkpoint = 0UL || not (streamName.StartsWith "cp-") then return None
                else
                    let! metadata = client.GetStreamMetadataAsync(streamName).ConfigureAwait(false)
                    let truncatedBefore =
                        if metadata.Metadata.TruncateBefore.HasValue then Some (metadata.Metadata.TruncateBefore.Value.ToUInt64()) else None
                    match truncatedBefore with
                    | Some before when checkpoint <= before ->
                        return Some (sprintf "%s was linked again from the start: its links up to checkpoint %d were deleted" streamName checkpoint)
                    | _ ->
                        let read = client.ReadStreamAsync(Direction.Backwards, streamName, StreamPosition.End, 1L)
                        match! read.ReadState with
                        | ReadState.StreamNotFound ->
                            return Some (sprintf "%s doesn't exist, but this projection is at checkpoint %d of it" streamName checkpoint)
                        | _ ->
                            let! last = read |> TaskSeq.toListAsync
                            match last with
                            | event :: _ when event.OriginalEventNumber.ToUInt64() + 1UL >= checkpoint -> return None
                            | _ -> return Some (sprintf "%s is shorter than checkpoint %d: it isn't the stream this projection was built from" streamName checkpoint)
            }
