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

type KurrentDBSubscription<'TEvent when 'TEvent :> IAggregateEvents>
    (logger: ILogger<KurrentDBSubscription<'TEvent>>, client: KurrentDBClient, upcasters: IEventUpcaster seq) =
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
    let MaxResubscriptionAttempts = 5
    let InitialResubscriptionDelay = TimeSpan.FromSeconds(1.0)

    // Exponential backoff before each resubscription attempt: 1s, 2s, 4s, 8s, 16s
    let ResubscriptionDelay (attempt: int) =
        TimeSpan.FromTicks(InitialResubscriptionDelay.Ticks * (1L <<< (attempt - 1)))

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

    new(logger: ILogger<KurrentDBSubscription<'TEvent>>, client: KurrentDBClient) =
        KurrentDBSubscription<'TEvent>(logger, client, Seq.empty)

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
            if resubscriptionAttempt > 0 then
                resubscriptionAttempt <- 0
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
                                            // Only progress resets the attempts: a handled event (HandleEvent) or catching up.
                                            // Not the confirmation, or a projection failing on the same event would retry forever
                                            | :? StreamMessage.CaughtUp ->
                                                resubscriptionAttempt <- 0
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
                        logger.LogCritical(unknown, "Projection {Name} on stream {StreamName} STOPPED on an event it has no case for.",
                            this.Name, this.StreamName)
                        ExceptionDispatchInfo.Capture(unknown).Throw()
                    | Some ex ->
                        resubscriptionAttempt <- resubscriptionAttempt + 1
                        RosettaTelemetry.Resubscriptions.Add(1L, RosettaTelemetry.Tag "rosetta.projection" this.Name)
                        if resubscriptionAttempt > MaxResubscriptionAttempts then
                            hasFailed <- true
                            error <- ex.Message
                            logger.LogCritical(ex, "FAILED to RESUBSCRIBE Projection {Name} on stream {StreamName} after {Attempts} attempts.",
                                this.Name, this.StreamName, MaxResubscriptionAttempts)
                            ExceptionDispatchInfo.Capture(ex).Throw()
                        else
                            let delay = ResubscriptionDelay resubscriptionAttempt
                            logger.LogError(ex, "Subscription {Name} on stream {StreamName} DROPPED. Resubscribing in {Delay} (attempt {Attempt} of {MaxAttempts}).",
                                this.Name, this.StreamName, delay, resubscriptionAttempt, MaxResubscriptionAttempts)
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