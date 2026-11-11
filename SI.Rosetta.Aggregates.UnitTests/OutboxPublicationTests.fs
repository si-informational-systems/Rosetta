namespace SI.Rosetta.Aggregates.UnitTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Common
open SI.Rosetta.Serialization

type Destination = { Street: string; City: string }

/// Renamed from ConsignmentDispatched: stored under its old name
[<EventType("ConsignmentDispatched")>]
type ConsignmentSent = { Id: string; Note: string option; Items: string list; To: Destination; Weight: decimal }

type ConsignmentCancelled = { Id: string; Reason: string }

[<RequireQualifiedAccess>]
type ConsignmentEvents =
    | Sent of ConsignmentSent
    | Cancelled of ConsignmentCancelled
    interface IAggregateEvents

/// An aggregate as the outbox sees it: its id, its version and the events it published
type ConsignmentAggregate(id: string, version: int64, published: IAggregateEvents list) =
    interface IAggregate with
        member _.Id = id
        member _.Version = version
        member _.Changes = List<IAggregateEvents>()
        member _.PublishedEvents = List<IAggregateEvents>(published)

/// Keeps what's logged, with its level
type CapturingLogger<'T>() =
    member val Entries = ConcurrentQueue<LogLevel * string>()
    interface ILogger<'T> with
        member _.BeginScope<'TState>(_state: 'TState) : IDisposable = null
        member _.IsEnabled(_level) = true
        member this.Log<'TState>(level: LogLevel, _eventId: EventId, state: 'TState, ex: exn, formatter: Func<'TState, exn, string>) =
            this.Entries.Enqueue((level, formatter.Invoke(state, ex)))

module private Consignments =
    let Sent =
        ConsignmentEvents.Sent
            { Id = "Consignments-1"; Note = Some "Fragile"; Items = [ "Rosetta stone"; "Lexicon" ]
              To = { Street = "Great Russell St"; City = "London" }; Weight = 760.5m }
    let Cancelled = ConsignmentEvents.Cancelled { Id = "Consignments-1"; Reason = "Not for sale" }
    let RecordOf (event: ConsignmentEvents) =
        match event with
        | ConsignmentEvents.Sent sent -> box sent
        | ConsignmentEvents.Cancelled cancelled -> box cancelled

    let Until (description: string) (condition: unit -> bool) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 15.0
            while not (condition ()) do
                if DateTime.UtcNow > deadline then raise (TimeoutException description)
                do! Task.Delay 20
        }

    let Options () = OutboxOptions(PublishPollInterval = TimeSpan.FromMilliseconds 20.0)

    /// Runs a relay over the outbox until condition holds
    let Relay (outbox: MemoryOutbox) (publisher: IOutboxPublisher) (upcasters: IEventUpcaster list) (logger: ILogger<OutboxRelay>) (description: string) (condition: unit -> bool) =
        task {
            use relay = new OutboxRelay(outbox, publisher, upcasters, Options (), logger)
            do! relay.StartAsync(CancellationToken.None)
            do! Until description condition
            do! relay.StopAsync(CancellationToken.None)
        }

open Consignments

/// The outbox records the types of its events, and its publisher is handed each event decoded
type OutboxPublicationTests() =
    [<Fact>]
    member this.``The events union and the record are named without a version, and resolve``() =
        let union = EventEncoding.EventsUnionClrTypeOf Sent
        let record = EventEncoding.EventClrTypeOf Sent

        // A case with fields is a class of its own, nested in the union: the union is named, not it
        Assert.NotEqual(typeof<ConsignmentEvents>, Sent.GetType())
        Assert.Equal("SI.Rosetta.Aggregates.UnitTests.ConsignmentEvents, SI.Rosetta.Aggregates.UnitTests", union)
        Assert.Equal("SI.Rosetta.Aggregates.UnitTests.ConsignmentSent, SI.Rosetta.Aggregates.UnitTests", record)
        // No version, culture or public key token: they resolve whatever the assembly's version
        Assert.DoesNotContain("Version=", union)
        Assert.DoesNotContain("Version=", record)
        Assert.Equal(typeof<ConsignmentEvents>, Type.GetType(union, true))
        Assert.Equal(typeof<ConsignmentSent>, Type.GetType(record, true))

    [<Fact>]
    member this.``The outbox records the events union and the record type of each event``() =
        let recorded = OutboxEvents.Of(ConsignmentAggregate("Consignments-1", 2L, [ Sent; Cancelled ]))

        Assert.Equal<string list>(
            [ "SI.Rosetta.Aggregates.UnitTests.ConsignmentEvents, SI.Rosetta.Aggregates.UnitTests"
              "SI.Rosetta.Aggregates.UnitTests.ConsignmentEvents, SI.Rosetta.Aggregates.UnitTests" ],
            recorded |> Array.map (fun event -> event.EventsUnionClrType) |> List.ofArray)
        Assert.Equal<string list>(
            [ "SI.Rosetta.Aggregates.UnitTests.ConsignmentSent, SI.Rosetta.Aggregates.UnitTests"
              "SI.Rosetta.Aggregates.UnitTests.ConsignmentCancelled, SI.Rosetta.Aggregates.UnitTests" ],
            recorded |> Array.map (fun event -> event.EventClrType) |> List.ofArray)
        Assert.Equal<string list>([ "ConsignmentDispatched"; "ConsignmentCancelled" ], recorded |> Array.map (fun event -> event.EventType) |> List.ofArray)

    [<Fact>]
    member this.``An event has the Id Rosetta 1.37 gave it``() =
        let aggregate = OrganizationAggregate()
        (aggregate :> IAggregateInstance<OrganizationCommands>).Execute(OrganizationCommands.Register { Id = "Organizations-1"; Name = "Rosetta" })

        let recorded = Assert.Single(OutboxEvents.Of aggregate)

        // What 1.37 recorded for it: the types the outbox records since don't change it
        Assert.Equal("""{"Id":"Organizations-1","Name":"Rosetta"}""", recorded.Data)
        Assert.Equal(Guid.Parse "50a79e97-d9fd-80a2-ac52-760540fd20da", recorded.Id)

    [<Fact>]
    member this.``KurrentDB's metadata names the events union and the record as the outbox does``() =
        let serialized = KurrentDBSerialization.Serialize(Sent, Dictionary<string, obj>())
        use metadata = JsonDocument.Parse(serialized.Metadata)
        let Header (name: string) = metadata.RootElement.GetProperty(name).GetString()

        let recorded = (OutboxEvents.Of(ConsignmentAggregate("Consignments-1", 1L, [ Sent ]))).[0]

        Assert.Equal(recorded.EventsUnionClrType, Header KurrentDBSerialization.EventsUnionClrTypeHeader)
        Assert.Equal(recorded.EventClrType, Header KurrentDBSerialization.EventClrTypeHeader)
        Assert.Equal("ConsignmentDispatched", Header KurrentDBSerialization.DiscriminatedUnionCaseTypeHeader)
        // The older headers are still written, and the union's is the union, not the case's class
        Assert.Equal(recorded.EventsUnionClrType, Header KurrentDBSerialization.EventsDUClrTypeNameHeader)
        Assert.Equal(typeof<ConsignmentSent>.AssemblyQualifiedName, Header KurrentDBSerialization.EventClrTypeNameHeader)

    [<Fact>]
    member this.``The publisher is handed the event as the aggregate published it, and everything known about it``() =
        task {
            let outbox = MemoryOutbox()
            let publisher = CollectingPublisher()
            let command = (new Activity("SendConsignment")).SetIdFormat(ActivityIdFormat.W3C).Start()
            let recorded = OutboxEvents.Of(ConsignmentAggregate("Consignments-1", 3L, [ Sent; Cancelled ]))
            command.Stop()
            outbox.Add recorded
            publisher.FailOnce.[recorded.[0].Id] <- true

            do! Relay outbox publisher [] NullLogger<OutboxRelay>.Instance "both events are marked" (fun () -> outbox.Marked.Count = 2)

            // Handed over again after it threw
            Assert.Equal<int list>([ 1; 2; 1 ], publisher.Attempted |> Seq.map (fun publication -> publication.Attempt) |> List.ofSeq)
            let published = publisher.Published |> List.ofSeq
            Assert.Equal<ConsignmentEvents list>([ Sent; Cancelled ], published |> List.map (fun publication -> publication.Event :?> ConsignmentEvents))
            for publication, event, original in List.zip3 published (List.ofArray recorded) [ Sent; Cancelled ] do
                Assert.Equal(RecordOf original, publication.EventRecord)
                Assert.Equal(RecordOf(original).GetType(), publication.EventRecordType)
                Assert.Equal(typeof<ConsignmentEvents>, publication.EventsUnionType)
                Assert.Equal(event.EventType, publication.EventTypeName)
                Assert.Equal(event.Data, publication.EventJson)
                Assert.Equal(event.Id, publication.EventId)
                Assert.Equal("Consignment", publication.AggregateName)
                Assert.Equal("Consignments-1", publication.AggregateId)
                Assert.Equal(3L, publication.AggregateVersion)
                Assert.Equal(event.Position, publication.PositionInCommand)
                Assert.Equal(event.RecordedAt, publication.RecordedAt)
                Assert.Equal(command.TraceId, publication.CommandTrace.TraceId)
                Assert.Equal(command.SpanId, publication.CommandTrace.SpanId)
            Assert.Equal("ConsignmentDispatched", published.[0].EventTypeName)
            Assert.Equal<int list>([ 0; 1 ], published |> List.map (fun publication -> publication.PositionInCommand))
        }

    [<Fact>]
    member this.``An event recorded by an older version is handed over as its current case``() =
        task {
            let outbox = MemoryOutbox()
            let publisher = CollectingPublisher()
            let current = OrganizationEvents.NameChanged { Id = "Organizations-1"; Name = "Rosetta 2" }
            // OrganizationNameChanged, as the older version recorded it
            let old =
                { (OutboxEvents.Of(ConsignmentAggregate("Organizations-1", 2L, [ current ]))).[0] with
                    EventClrType = "SI.Rosetta.Aggregates.UnitTests.OrganizationRenamed, SI.Rosetta.Aggregates.UnitTests"
                    EventType = "OrganizationRenamed"
                    Data = """{"Id":"Organizations-1","NewName":"Rosetta 2"}""" }
            outbox.Add [ old ]

            do! Relay outbox publisher [ OrganizationRenamedUpcaster() ] NullLogger<OutboxRelay>.Instance "the event is marked" (fun () -> outbox.Marked.Count = 1)

            let publication = Assert.Single(publisher.Published)
            Assert.Equal(current, publication.Event :?> OrganizationEvents)
            Assert.Equal(box ({ Id = "Organizations-1"; Name = "Rosetta 2" } : OrganizationNameChanged), publication.EventRecord)
            Assert.Equal(typeof<OrganizationNameChanged>, publication.EventRecordType)
            // As stored
            Assert.Equal("OrganizationRenamed", publication.EventTypeName)
            Assert.Equal(old.Data, publication.EventJson)
        }

    [<Fact>]
    member this.``Decoding fails when the record names no union, a union that doesn't resolve, or a case it doesn't have``() =
        let recorded = (OutboxEvents.Of(ConsignmentAggregate("Consignments-1", 1L, [ Sent ]))).[0]
        let Decode = OutboxEvents.Decode Seq.empty

        Assert.Equal(Ok Sent, Decode recorded |> Result.map (fun event -> event :?> ConsignmentEvents))
        Assert.Equal(Error OutboxDecodeError.NoEventsUnion, Decode { recorded with EventsUnionClrType = null })
        Assert.Equal(
            Error (OutboxDecodeError.UnknownEventsUnion "Gone.ConsignmentEvents, Gone"),
            Decode { recorded with EventsUnionClrType = "Gone.ConsignmentEvents, Gone" })
        // A type, but not an events union
        Assert.Equal(
            Error (OutboxDecodeError.UnknownEventsUnion "SI.Rosetta.Aggregates.UnitTests.ConsignmentSent, SI.Rosetta.Aggregates.UnitTests"),
            Decode { recorded with EventsUnionClrType = recorded.EventClrType })
        Assert.Equal(
            Error (OutboxDecodeError.UnknownEventType(typeof<ConsignmentEvents>, "ConsignmentLost")),
            Decode { recorded with EventType = "ConsignmentLost" })

    [<Fact>]
    member this.``An event that can't be decoded is held with the events after it, until it's deleted or fixed``() =
        task {
            let outbox = MemoryOutbox()
            let publisher = CollectingPublisher()
            let logger = CapturingLogger<OutboxRelay>()
            let At (seconds: float) = DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc).AddSeconds seconds
            let Recorded (id: string) (seconds: float) =
                { (OutboxEvents.Of(ConsignmentAggregate(id, 1L, [ ConsignmentEvents.Cancelled { Id = id; Reason = "Lost" } ]))).[0] with RecordedAt = At seconds }
            let first = Recorded "Consignments-1" 0.0
            // Recorded by Rosetta 1.37
            let older = { Recorded "Consignments-2" 1.0 with EventsUnionClrType = null; EventClrType = null }
            let fixable = Recorded "Consignments-3" 2.0
            let renamed = { fixable with EventsUnionClrType = "Gone.ConsignmentEvents, Gone" }
            let last = Recorded "Consignments-4" 3.0
            outbox.Add [ first; older; renamed; last ]
            use relay = new OutboxRelay(outbox, publisher, Options (), logger)
            let PublishedIds () = publisher.Published |> Seq.map (fun publication -> publication.EventId) |> List.ofSeq
            let Logged (text: string) = logger.Entries |> Seq.exists (fun (level, message) -> level = LogLevel.Error && message.Contains text)

            do! relay.StartAsync(CancellationToken.None)
            do! Until "the older event isn't decoded" (fun () -> Logged "cannot decode ConsignmentCancelled as (null)")
            do! Task.Delay 300
            Assert.Equal<Guid list>([ first.Id ], PublishedIds ())
            Assert.Equal<Guid list>([ first.Id ], outbox.Marked |> List.ofSeq)
            Assert.Contains(logger.Entries, fun (_, message) -> message.Contains "Rosetta 1.37's publisher")

            outbox.Delete older.Id
            do! Until "the renamed union isn't decoded" (fun () -> Logged "cannot decode ConsignmentCancelled as Gone.ConsignmentEvents, Gone")
            Assert.Equal<Guid list>([ first.Id ], PublishedIds ())

            // Published in the order they were recorded, whatever the order the outbox holds them in
            outbox.Delete renamed.Id
            outbox.Add [ fixable ]
            do! Until "the rest is marked" (fun () -> outbox.Marked.Count = 3)
            do! relay.StopAsync(CancellationToken.None)

            Assert.Equal<Guid list>([ first.Id; fixable.Id; last.Id ], PublishedIds ())
        }

[<Collection("Telemetry")>]
type OutboxPublicationTelemetryTests() =
    [<Fact>]
    member this.``The publish span names the case, and undecodable events are counted``() =
        task {
            use listener = new RosettaListener()
            let outbox = MemoryOutbox()
            let recorded = (OutboxEvents.Of(ConsignmentAggregate("Consignments-9", 1L, [ Sent ]))).[0]
            outbox.Add [ recorded; { recorded with Id = Guid.NewGuid(); Position = 1; EventsUnionClrType = null } ]

            do! Relay outbox (CollectingPublisher()) [] NullLogger<OutboxRelay>.Instance "the event is marked" (fun () ->
                outbox.Marked.Count = 1 && not (Array.isEmpty (listener.Measured "rosetta.outbox.published" [ "rosetta.outcome", box "undecodable" ])))

            let span = listener.Activities |> Array.find (fun activity -> activity.DisplayName = "Consignment ConsignmentDispatched publish" && activity.Status <> ActivityStatusCode.Error)
            Assert.Equal(box "Sent", span.GetTagItem "rosetta.event.case")
            Assert.Equal<float>([| 1.0 |], listener.Measured "rosetta.outbox.published" [ "rosetta.aggregate", box "Consignment"; "rosetta.outcome", box "published" ])
        }
