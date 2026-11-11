namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Generic
open System.Text
open Xunit
open KurrentDB.Client
open SI.Rosetta.Common
open SI.Rosetta.Serialization

/// OrganizationRegistered as an older version of the domain wrote it: another record name, and FullName for Name
type RenamedOrganizationUpcaster() =
    interface IEventUpcaster with
        member _.Upcast(ev) =
            match ev.TypeName with
            | "OrganizationCreated" -> { ev.RenameField("FullName", "Name") with TypeName = "OrganizationRegistered" }
            | _ -> ev

/// Renamed from OrganizationArchived: the [<EventType>] keeps the events stored under the old name readable
[<EventType("OrganizationArchived")>]
type OrganizationClosed = { Id: string; Reason: string }

[<RequireQualifiedAccess>]
type ClosingEvents =
    | Closed of OrganizationClosed
    interface IAggregateEvents

/// Stored as OrganizationArchived too, by its type name
type OrganizationArchived = { Id: string }

[<RequireQualifiedAccess>]
type ClashingEvents =
    | Closed of OrganizationClosed
    | Archived of OrganizationArchived
    interface IAggregateEvents

type SerializationTests() =
    let StreamName = "Organizations-1"

    /// The event as KurrentDB returns it, from its data and metadata
    let Resolved (eventType: string) (data: byte[]) (metadata: byte[]) =
        let systemMetadata = dict [
            "type", eventType
            "created", string DateTime.UtcNow.Ticks
            "content-type", "application/json" ]
        let record =
            EventRecord(StreamName, Uuid.NewUuid(), StreamPosition.FromInt64(4L), Position(0UL, 0UL),
                systemMetadata, ReadOnlyMemory(data), ReadOnlyMemory(metadata))
        ResolvedEvent(record, null, Nullable())

    let Serialized (ev: IAggregateEvents) =
        let serialized = KurrentDBSerialization.Serialize(ev, Dictionary<string, obj>())
        Resolved serialized.EventClrName serialized.Data serialized.Metadata

    /// An event stored under typeName, without metadata
    let Written (typeName: string) (json: string) =
        Resolved typeName (Encoding.UTF8.GetBytes json) [||]


    let Registered = OrganizationEvents.Registered { Id = StreamName; Name = "Rosetta"; DateRegistered = DateTime(2026, 10, 15, 8, 30, 0) }

    [<Fact>]
    member this.``An event round-trips``() =
        Assert.Equal(Registered, KurrentDBSerialization.Deserialize<OrganizationEvents>(Serialized Registered))

    [<Fact>]
    member this.``An event its DU has no case for throws UnknownEventException``() =
        let unknown = Written "OrganizationArchived" """{"Id":"Organizations-1"}"""

        let ex = Assert.Throws<UnknownEventException>(fun () -> KurrentDBSerialization.Deserialize<OrganizationEvents>(unknown) |> ignore)

        Assert.Equal("OrganizationArchived", ex.TypeName)
        Assert.Equal(typeof<OrganizationEvents>, ex.EventsType)
        Assert.Equal(StreamName, ex.StreamName)
        Assert.Equal(4UL, ex.EventNumber)

    [<Fact>]
    member this.``TryDeserializeWith returns None for an event its DU has no case for``() =
        let unknown = Written "OrganizationArchived" """{"Id":"Organizations-1"}"""
        Assert.Equal(None, KurrentDBSerialization.TryDeserializeWith<OrganizationEvents> [] unknown)

    [<Fact>]
    member this.``An upcaster turns an old event into the current case``() =
        let old = Written "OrganizationCreated" """{"Id":"Organizations-1","FullName":"Rosetta","DateRegistered":"2026-10-15T08:30:00"}"""

        let ev = KurrentDBSerialization.DeserializeWith<OrganizationEvents> [ RenamedOrganizationUpcaster() :> IEventUpcaster ] old

        Assert.Equal(Registered, ev)

    [<Fact>]
    member this.``Upcasting leaves the events it doesn't apply to unchanged, date-like strings included``() =
        let dateLikeName = OrganizationEvents.NameChanged { Id = StreamName; Name = "2026-10-15T08:30:00" }

        let upcaster = RenamedOrganizationUpcaster() :> IEventUpcaster
        Assert.Equal(Registered, KurrentDBSerialization.DeserializeWith<OrganizationEvents> [ upcaster ] (Serialized Registered))
        Assert.Equal(dateLikeName, KurrentDBSerialization.DeserializeWith<OrganizationEvents> [ upcaster ] (Serialized dateLikeName))

    [<Fact>]
    member this.``A record with an [<EventType>] is stored under that name``() =
        let serialized = KurrentDBSerialization.Serialize(ClosingEvents.Closed { Id = StreamName; Reason = "Merged" }, Dictionary<string, obj>())

        Assert.Equal("OrganizationArchived", serialized.EventClrName)

    [<Fact>]
    member this.``An event is stored without metadata, inside a trace too``() =
        use command = (new System.Diagnostics.Activity("RegisterOrganization")).Start()

        let serialized = KurrentDBSerialization.Serialize(Registered, Dictionary<string, obj>())

        Assert.Equal("OrganizationRegistered", serialized.EventClrName)
        Assert.Empty(serialized.Metadata)
        Assert.Equal(Registered, KurrentDBSerialization.Deserialize<OrganizationEvents>(Serialized Registered))

    [<Fact>]
    member this.``Events stored under the [<EventType>] name are read into the renamed record``() =
        let stored = Written "OrganizationArchived" """{"Id":"Organizations-1","Reason":"Merged"}"""

        Assert.Equal(ClosingEvents.Closed { Id = StreamName; Reason = "Merged" }, KurrentDBSerialization.Deserialize<ClosingEvents> stored)

    [<Fact>]
    member this.``A DU with two cases stored under one name can't be read``() =
        let stored = Written "OrganizationArchived" """{"Id":"Organizations-1"}"""

        let ex = Assert.Throws<InvalidOperationException>(fun () -> KurrentDBSerialization.Deserialize<ClashingEvents> stored |> ignore)

        Assert.Contains("ClashingEvents", ex.Message)
        Assert.Contains("OrganizationArchived", ex.Message)

    [<Fact>]
    member this.``Events from before a field was added or removed are still read``() =
        // Name was added after this event was stored, and Retired was removed
        let stored = Written "OrganizationNameChanged" """{"Id":"Organizations-1","Retired":true}"""

        match KurrentDBSerialization.Deserialize<OrganizationEvents> stored with
        | OrganizationEvents.NameChanged changed ->
            Assert.Equal(StreamName, changed.Id)
            Assert.Null(changed.Name)
        | other -> failwithf "Read as %A" other

    [<Fact>]
    member this.``The trace KurrentDB.Client wrote in an event's metadata is continued, as sampled``() =
        let stored =
            Resolved "OrganizationNameChanged" (Encoding.UTF8.GetBytes """{"Id":"Organizations-1","Name":"Rosetta"}""")
                (Encoding.UTF8.GetBytes """{"$traceId":"4bf92f3577b34da6a3ce929d0e0e4736","$spanId":"00f067aa0ba902b7"}""")

        let context = KurrentDBSerialization.TraceContextOf stored

        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", context.TraceId.ToHexString())
        Assert.Equal("00f067aa0ba902b7", context.SpanId.ToHexString())
        Assert.Equal(System.Diagnostics.ActivityTraceFlags.Recorded, context.TraceFlags)
        Assert.True(context.IsRemote)

    [<Fact>]
    member this.``Metadata that isn't a trace gives no trace context``() =
        let WithMetadata (metadata: string) =
            Resolved "OrganizationNameChanged" (Encoding.UTF8.GetBytes """{"Id":"Organizations-1","Name":"Rosetta"}""") (Encoding.UTF8.GetBytes metadata)

        for metadata in [ "{}"; "[]"; "not json"; """{"$traceId":"4bf92f3577b34da6a3ce929d0e0e4736"}"""; """{"$traceId":"nope","$spanId":"nope"}""" ] do
            Assert.Equal(System.Diagnostics.ActivityContext(), KurrentDBSerialization.TraceContextOf(WithMetadata metadata))

    [<Fact>]
    member this.``An event stored outside a trace has no trace context``() =
        let stored = Written "OrganizationNameChanged" """{"Id":"Organizations-1","Name":"Rosetta"}"""

        Assert.Equal(System.Diagnostics.ActivityContext(), KurrentDBSerialization.TraceContextOf stored)

type CustomProjectionDefinitionTests() =
    [<Fact>]
    member this.``A Custom Projection links events by their stored name``() =
        let definition =
            SI.Rosetta.Projections.KurrentDB.KurrentDBProjectionBuilder.BuildProjectionDefinition {
                ProjectionName = "ClosingsCustomProjection"
                DestinationStreamName = "cp-Closings"
                AggregateEventsStreamNames = [ OrganizationStream ]
                EventsToInclude = [| typeof<OrganizationClosed> |] }

        Assert.Equal("fromStreams('$ce-Organizations').when({OrganizationArchived: function(s,e){linkTo('cp-Closings', e);return s;}})", definition.Source)
