namespace SI.Rosetta.Projections.EventStore

open System
open System.Threading.Tasks
open EventStore.Client
open Microsoft.Extensions.Logging
open SI.Rosetta.Projections
open SI.Rosetta.Common
open FSharp.Control
open SI.Rosetta.Serialization

type ESSubscription<'TEvent when 'TEvent :> IEvents>
    (logger: ILogger<ESSubscription<'TEvent>>, client: EventStoreClient) =
    let mutable name = String.Empty
    let mutable streamName = String.Empty
    let mutable currentCheckpoint = 0UL
    let mutable resubscriptionAttempt = 0
    let mutable hasFailed = false
    let mutable error = String.Empty
    let mutable eventReceived: 'TEvent * uint64 -> Task<unit> = fun (ev, cp) -> Task.FromResult<unit>()
    let MaxResubscriptionAttempts = 5

    let isNotDeleted (event: ResolvedEvent) =
        event.Event <> null

    member private this.handleEvent<'T when 'T :> IEvents> (event: ResolvedEvent) =
        task {
            let oneBasedCheckPoint = event.OriginalEventNumber.ToUInt64() + 1UL
            if isNotDeleted event then
                let ev = EventStoreSerialization.Deserialize event
                do! eventReceived(ev, oneBasedCheckPoint).ConfigureAwait(false)
            
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

        member this.StartAsync(oneBasedCheckpoint) =
            task {
                currentCheckpoint <- oneBasedCheckpoint
                let mutable checkpoint = 
                    if currentCheckpoint = 0UL then 
                        FromStream.Start 
                    else 
                        let startingCheckpoint = currentCheckpoint - 1UL
                        FromStream.After(startingCheckpoint)

                let rec subscribe() =
                    task {
                        try
                            use subscription = client.SubscribeToStream(
                                    this.StreamName,
                                    checkpoint,
                                    resolveLinkTos = true)

                            let asyncSubscription = AsyncSeq.ofAsyncEnum subscription.Messages

                            logger.LogInformation(
                                "Projection {Name} STARTED on stream {StreamName}.",
                                this.Name, this.StreamName)

                            do! asyncSubscription
                                |> AsyncSeq.foldAsync (fun _ message ->
                                    async {
                                        match message with
                                        | :? StreamMessage.Event as evnt ->
                                            do! this.handleEvent<'TEvent> evnt.ResolvedEvent |> Async.AwaitTask
                                            checkpoint <- FromStream.After(evnt.ResolvedEvent.OriginalEventNumber)
                                            resubscriptionAttempt <- 0
                                        | _ -> ()
                                        return ()
                                    }) ()

                        with
                        | :? AggregateException as ex when (ex.InnerException :? OperationCanceledException) -> 
                            logger.LogInformation($"Subscription {this.Name} on stream {this.StreamName} was CANCELED.")
                        | :? OperationCanceledException ->
                            logger.LogInformation($"Subscription {this.Name} on stream {this.StreamName} was CANCELED.")

                        | :? AggregateException as ex when (ex.InnerException :? ObjectDisposedException) -> 
                            logger.LogInformation($"Subscription {this.Name} on stream {this.StreamName} was CANCELED by the user.")
                        | :? ObjectDisposedException ->
                            logger.LogInformation($"Subscription {this.Name} on stream {this.StreamName} was CANCELED by the user.")

                        | ex ->
                            logger.LogError("Subscription {Name}-{StreamName} DROPPED: {Exception}", this.Name, this.StreamName, ex)
                            resubscriptionAttempt <- resubscriptionAttempt + 1
                            if resubscriptionAttempt < MaxResubscriptionAttempts then
                                return! subscribe().ConfigureAwait(false)
                            else
                                hasFailed <- true
                                error <- ex.Message
                                logger.LogCritical(ex, "FAILED to RESUBSCRIBE Projection: {Name}-{StreamName}", 
                                    this.Name, this.StreamName)
                                raise ex
                    }
                do! subscribe().ConfigureAwait(false)
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