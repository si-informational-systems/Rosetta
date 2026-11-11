namespace SI.Rosetta.Aggregates

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open SI.Rosetta.Common

/// The outbox's settings. UseOutboxWith/AddOutboxWith read them from the Outbox configuration section
[<AllowNullLiteral>]
type OutboxOptions() =
    static member val DefaultCollectionName = "Outbox"
    /// The collection the events are recorded in (Outbox:CollectionName; Outbox when it isn't set)
    member val CollectionName = OutboxOptions.DefaultCollectionName with get, set
    /// How long a recorded event is kept (Outbox:EventTTLMinutes); null keeps the events until something deletes them
    member val EventTTL = Nullable<TimeSpan>() with get, set
    /// 36 hours: the shortest expiration frequency every RavenDB licence allows, Community included
    static member val DefaultExpirationFrequency = TimeSpan.FromHours 36.0
    /// How often RavenDB deletes the expired documents (Outbox:ExpirationFrequencyMinutes; 36 hours, which every
    /// licence allows, when it isn't set), when the outbox turns the database's document expiration on for EventTTL.
    /// A database whose document expiration is already on keeps its own frequency. PostgreSQL, which has no TTL: how
    /// often, at most, the outbox deletes the expired events itself, once it has recorded events. MongoDB's TTL monitor
    /// deletes the expired documents every 60 seconds by itself
    member val ExpirationFrequency = OutboxOptions.DefaultExpirationFrequency with get, set
    /// With a publisher (UseOutboxPublisher): how many events it reads at a time (Outbox:PublishBatchSize; 100)
    member val PublishBatchSize = 100 with get, set
    /// With a publisher: how long it waits before looking again when there's nothing to publish
    /// (Outbox:PublishPollIntervalMilliseconds; 1 second)
    member val PublishPollInterval = TimeSpan.FromSeconds 1.0 with get, set
    /// With a publisher: how long its lease lasts unless renewed. One instance of the app publishes at a time, the one
    /// holding the lease; another takes over once it expires (30 seconds)
    member val PublishLeaseDuration = TimeSpan.FromSeconds 30.0 with get, set
    /// Set when a publisher is registered: an event then expires EventTTL after it's published, not after it's
    /// recorded, so an event waiting to be published never expires
    member val ExpiresFromPublication = false with get, set

    member this.Validate() =
        if String.IsNullOrWhiteSpace this.CollectionName then
            raise (InvalidOperationException "The outbox's CollectionName can't be empty.")
        if this.EventTTL.HasValue && this.EventTTL.Value <= TimeSpan.Zero then
            raise (InvalidOperationException(sprintf "The outbox's EventTTL must be positive, not %O." this.EventTTL.Value))
        // RavenDB counts it in whole seconds
        if this.ExpirationFrequency < TimeSpan.FromSeconds 1.0 then
            raise (InvalidOperationException(sprintf "The outbox's ExpirationFrequency must be at least a second, not %O." this.ExpirationFrequency))
        if this.PublishBatchSize < 1 then
            raise (InvalidOperationException(sprintf "The outbox's PublishBatchSize must be at least 1, not %d." this.PublishBatchSize))
        if this.PublishPollInterval <= TimeSpan.Zero then
            raise (InvalidOperationException(sprintf "The outbox's PublishPollInterval must be positive, not %O." this.PublishPollInterval))
        if this.PublishLeaseDuration < TimeSpan.FromSeconds 1.0 then
            raise (InvalidOperationException(sprintf "The outbox's PublishLeaseDuration must be at least a second, not %O." this.PublishLeaseDuration))

/// A PublishedEvent as the outbox records it, whichever store holds it
type OutboxEvent =
    { /// The event's identity: the same event, published again at the same version and position (by a repeat of the
      /// command), has the same Id, so the outbox records it once. Also a message id for whatever consumes it
      Id: Guid
      /// The aggregate that published it, as the traces and metrics name it (Person for PersonAggregate)
      Aggregate: string
      AggregateId: string
      /// The aggregate's version after the command that published the event
      Version: int64
      /// The event's place among the events its command published, from 0
      Position: int
      /// The events union the event is a case of, as "Namespace.Type, Assembly" (EventEncoding.EventsUnionClrTypeOf):
      /// what OutboxEvents.Decode reads the event back as. Null in a record from before Rosetta 1.38
      EventsUnionClrType: string
      /// The event's record type, the case's data, as "Namespace.Type, Assembly" (EventEncoding.EventClrTypeOf), as it
      /// was when the event was recorded: for tools, and for reading a record without its union. Null in a record from
      /// before Rosetta 1.38
      EventClrType: string
      /// The name the event's record is stored under: its [<EventType>] name, or else its type name. With Data, it's
      /// what EventEncoding.Decode reads the event back from
      EventType: string
      /// The event's record as JSON, as KurrentDB stores it
      Data: string
      /// When it was first recorded, in UTC: with Position, the order the events were recorded in
      RecordedAt: DateTime
      /// The W3C trace context of the command, so whatever publishes the event can continue its trace; null without one
      TraceParent: string
      TraceState: string }

/// Where the PublishedEvents of aggregates are recorded, for whatever publishes them to the outside world
type IOutbox =
    /// Records the events it doesn't hold yet (by Id), and leaves the ones it does as they are. Returns how many it recorded
    abstract member RecordAsync: events: OutboxEvent[] -> Task<int>

/// A repository with an outbox, which records the PublishedEvents of the aggregates it stores. A command that changes
/// nothing, e.g. an idempotent repeat, stores nothing, so its handler records the events it published with
/// RecordPublishedEventsAsync. An idempotent branch that publishes its events again therefore recovers the events of
/// an attempt that stored the aggregate but failed before recording them; the ones that were recorded stay as they are
type IRecordPublishedEvents =
    abstract member RecordPublishedEventsAsync: aggregate: IAggregate -> Task

/// An event the outbox's publisher hands over to be published: the event itself, and everything known about it.
/// Rosetta builds it for each PublishAsync call; it's never stored
type OutboxPublication =
    { // The event
      /// The event as the aggregate published it: a case of its events union, decoded through the registered upcasters
      Event: IAggregateEvents
      /// The event's record, the case's data as a plain .NET value (a ReservationMade)
      EventRecord: obj
      /// The record's type (ReservationMade), after upcasting
      EventRecordType: Type
      /// The events union it's a case of (ReservationEvents)
      EventsUnionType: Type
      /// The name the record is stored under: its [<EventType>] name, or else its type name
      EventTypeName: string
      /// The record as JSON, exactly as stored, for forwarding the bytes as they are
      EventJson: string

      // Its identity
      /// The same for every repeat of the event: give it to the message as its id, so consumers can drop duplicates
      EventId: Guid

      // Where it came from
      /// The aggregate that published it, as Rosetta names it (Reservation)
      AggregateName: string
      AggregateId: string
      /// The aggregate's version after the command that published the event
      AggregateVersion: int64
      /// The event's place among the events its command published, from 0
      PositionInCommand: int
      /// When the outbox recorded it, in UTC
      RecordedAt: DateTime
      /// The trace of the command that published it. While PublishAsync runs, Activity.Current is Rosetta's publish
      /// span, a child of this one: propagate Activity.Current to continue the trace through the broker
      CommandTrace: ActivityContext

      // This attempt
      /// 1 the first time the event is handed over, more when earlier attempts threw
      Attempt: int }

/// Sends the outbox's events to the outside world (a message broker, e.g. RabbitMQ): the app implements it, and
/// UseOutboxPublisher runs it. Called one event at a time, in the order they were recorded, each decoded into its
/// events union. Delivery is at least once: an event whose publication wasn't marked (a crash in between) is published
/// again, so give the message the publication's EventId as its message id and let consumers ignore one they've seen.
/// A thrown exception is retried, from that event on. Which value to send: Event for a typed bus that routes per union
/// (Rebus, MassTransit), EventRecord to route per event or map it to a contract, EventJson with EventTypeName to
/// forward the bytes as they are (raw RabbitMQ, Kafka)
type IOutboxPublisher =
    abstract member PublishAsync: publication: OutboxPublication * cancellationToken: CancellationToken -> Task

/// Why an outbox event can't be read back as its events union
[<RequireQualifiedAccess>]
type OutboxDecodeError =
    /// The record names no events union: it was recorded before Rosetta 1.38
    | NoEventsUnion
    /// The events union doesn't resolve: its assembly isn't loaded in this process, or the type was renamed
    | UnknownEventsUnion of eventsUnionClrType: string
    /// The events union has no case for the event's type name, after the upcasters
    | UnknownEventType of eventsUnion: Type * eventType: string

    /// What went wrong, and what fixes it
    member this.Reason =
        match this with
        | NoEventsUnion ->
            "it was recorded before Rosetta 1.38, which didn't record its events union. Publish it with Rosetta 1.37's publisher, or delete it"
        | UnknownEventsUnion name ->
            sprintf "the events union %s doesn't resolve: its assembly isn't loaded in this process, or the type was renamed. Load the assembly, or publish or delete the event" name
        | UnknownEventType (union, eventType) ->
            sprintf "%s has no case for the event type %s, after the upcasters. Register an upcaster for it, or publish or delete the event" union.Name eventType

/// An outbox event that can't be read back as its events union: the outbox's publisher holds it, and the events after
/// it, until it's fixed or deleted
type OutboxDecodeException(event: OutboxEvent, error: OutboxDecodeError) =
    inherit Exception(
        sprintf "Cannot decode %s as %s: %s." event.EventType
            (if String.IsNullOrEmpty event.EventsUnionClrType then "(no events union)" else event.EventsUnionClrType) error.Reason)
    member this.Event = event
    member this.Error = error

/// The outbox as its publisher reads it: Rosetta's outboxes implement it
type IOutboxRelayStore =
    /// Takes or renews the lease that lets one instance of the app publish (true), unless another owner holds it
    abstract member TryLeaseAsync: owner: string * duration: TimeSpan * cancellationToken: CancellationToken -> Task<bool>
    /// The oldest events not published yet, at most max, in the order they were recorded
    abstract member UnpublishedAsync: max: int * cancellationToken: CancellationToken -> Task<OutboxEvent[]>
    /// Marks the events published. They're kept, so a repeat of their command doesn't record them again, until EventTTL
    /// after publishedAt
    abstract member MarkPublishedAsync: events: OutboxEvent[] * publishedAt: DateTime * cancellationToken: CancellationToken -> Task

module OutboxEvents =
    /// A UUID v8 (RFC 9562) from the SHA-256 of what makes the event the one it is: the aggregate, its version, the
    /// event's position in its command, its type and its data
    let IdOf (aggregate: string) (aggregateId: string) (version: int64) (position: int) (eventType: string) (data: string) =
        let identity = String.Join("\u0000", aggregate, aggregateId, string version, string position, eventType, data)
        let bytes = SHA256.HashData(Encoding.UTF8.GetBytes identity).[0..15]
        bytes.[6] <- (bytes.[6] &&& 0x0Fuy) ||| 0x80uy
        bytes.[8] <- (bytes.[8] &&& 0x3Fuy) ||| 0x80uy
        Guid(bytes, bigEndian = true)

    /// The aggregate's PublishedEvents as the outbox records them
    let Of (aggregate: IAggregate) : OutboxEvent[] =
        let recordedAt = DateTime.UtcNow
        let traceParent, traceState =
            match Activity.Current with
            | null -> null, null
            | activity -> activity.Id, (if String.IsNullOrEmpty activity.TraceStateString then null else activity.TraceStateString)
        let aggregateName = AggregateNameOf (aggregate.GetType())
        aggregate.PublishedEvents
        |> Seq.mapi (fun position event ->
            let eventType, data = EventEncoding.Encode event
            { Id = IdOf aggregateName aggregate.Id aggregate.Version position eventType data
              Aggregate = aggregateName
              AggregateId = aggregate.Id
              Version = aggregate.Version
              Position = position
              EventsUnionClrType = EventEncoding.EventsUnionClrTypeOf event
              EventClrType = EventEncoding.EventClrTypeOf event
              EventType = eventType
              Data = data
              RecordedAt = recordedAt
              TraceParent = traceParent
              TraceState = traceState })
        |> Seq.toArray

    /// The events in the order they're published: as recorded (RecordedAt, then Position), except that each aggregate's
    /// events keep their own order (Version, then Position) in the places its events take, should clocks of the app's
    /// instances disagree
    let InPublishingOrder (events: OutboxEvent[]) : OutboxEvent[] =
        let recorded = events |> Array.sortBy (fun event -> event.RecordedAt, event.AggregateId, event.Version, event.Position)
        let byAggregate =
            recorded
            |> Array.groupBy (fun event -> event.Aggregate, event.AggregateId)
            |> Array.map (fun (key, events) -> key, Collections.Generic.Queue(events |> Array.sortBy (fun event -> event.Version, event.Position)))
            |> dict
        recorded |> Array.map (fun event -> byAggregate.[(event.Aggregate, event.AggregateId)].Dequeue())

    let private Unions = ConcurrentDictionary<string, Type>()

    /// The events union a record names, resolved once. A name that doesn't resolve is looked up again the next time:
    /// its assembly may be loaded by then
    let private UnionOf (name: string) =
        match Unions.TryGetValue name with
        | true, union -> Some union
        | false, _ ->
            match Type.GetType(name, false) with
            | null -> None
            | union when FSharpType.IsUnion union && typeof<IAggregateEvents>.IsAssignableFrom union ->
                Unions.[name] <- union
                Some union
            | _ -> None

    /// The event as the aggregate published it, a case of the events union it was recorded with, read through the
    /// upcasters, as the outbox's publisher reads it. The record is the case's field
    let Decode (upcasters: IEventUpcaster seq) (event: OutboxEvent) : Result<IAggregateEvents, OutboxDecodeError> =
        if String.IsNullOrEmpty event.EventsUnionClrType then Error OutboxDecodeError.NoEventsUnion
        else
            match UnionOf event.EventsUnionClrType with
            | None -> Error (OutboxDecodeError.UnknownEventsUnion event.EventsUnionClrType)
            | Some union ->
                match EventEncoding.DecodeAs union upcasters event.EventType event.Data with
                | Ok decoded -> Ok decoded
                | Error eventType -> Error (OutboxDecodeError.UnknownEventType(union, eventType))

    /// Counts the events the outbox recorded in rosetta.outbox.events
    let Recorded (aggregate: string) (recorded: int) =
        if recorded > 0 then
            RosettaTelemetry.OutboxEvents.Add(int64 recorded, RosettaTelemetry.Tag "rosetta.aggregate" aggregate)

    /// Records the aggregate's PublishedEvents in the outbox, on their own
    let RecordAsync (outbox: IOutbox) (aggregate: IAggregate) : Task =
        task {
            let events = Of aggregate
            if events.Length > 0 then
                let! recorded = outbox.RecordAsync(events).ConfigureAwait(false)
                Recorded events.[0].Aggregate recorded
        }

/// A repository with the outbox behind it: once it has stored an aggregate, the aggregate's PublishedEvents are
/// recorded. For stores the outbox can't share a transaction with, e.g. KurrentDB and RavenDB: a failure between the
/// two leaves the aggregate stored and its events unrecorded, until a repeat of the command publishes them again
type OutboxAggregateRepository(repository: IAggregateRepository, outbox: IOutbox) =
    interface IAggregateRepository with
        member this.StoreAsync<'TAggregateState
                                when 'TAggregateState :> IAggregateState>
                                (aggregate: IAggregate<'TAggregateState>) =
            task {
                do! repository.StoreAsync<'TAggregateState>(aggregate).ConfigureAwait(false)
                do! OutboxEvents.RecordAsync outbox aggregate
            }

        member this.GetAsync<'TAggregateState
                                when 'TAggregateState : (new : unit -> 'TAggregateState)
                                and 'TAggregateState :> IAggregateState>
            (id: string, version: int64) =
            repository.GetAsync<'TAggregateState>(id, version)

    interface IRecordPublishedEvents with
        member this.RecordPublishedEventsAsync(aggregate) = OutboxEvents.RecordAsync outbox aggregate

    interface IEventSourcedAggregateRepository
    interface IStateBasedAggregateRepository
