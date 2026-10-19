namespace SI.Rosetta.Aggregates

open System
open System.Diagnostics
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open SI.Rosetta.Common

/// The outbox's settings. UseOutboxWith/AddOutboxWith read them from the Outbox configuration section
[<AllowNullLiteral>]
type OutboxOptions() =
    static member val DefaultCollectionName = "Outbox"
    /// The collection the events are recorded in (Outbox:CollectionName; Outbox when it isn't set)
    member val CollectionName = OutboxOptions.DefaultCollectionName with get, set
    /// How long a recorded event is kept (Outbox:EventTTLMinutes); null keeps the events until something deletes them
    member val EventTTL = Nullable<TimeSpan>() with get, set

    member this.Validate() =
        if String.IsNullOrWhiteSpace this.CollectionName then
            raise (InvalidOperationException "The outbox's CollectionName can't be empty.")
        if this.EventTTL.HasValue && this.EventTTL.Value <= TimeSpan.Zero then
            raise (InvalidOperationException(sprintf "The outbox's EventTTL must be positive, not %O." this.EventTTL.Value))

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
              EventType = eventType
              Data = data
              RecordedAt = recordedAt
              TraceParent = traceParent
              TraceState = traceState })
        |> Seq.toArray

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
