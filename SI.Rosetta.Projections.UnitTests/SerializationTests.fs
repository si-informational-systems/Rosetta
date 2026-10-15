namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Generic
open System.Text
open Newtonsoft.Json.Linq
open Xunit
open KurrentDB.Client
open SI.Rosetta.Common
open SI.Rosetta.Serialization

/// OrganizationRegistered as an older version of the domain wrote it: another record name, and FullName for Name
type RenamedOrganizationUpcaster() =
    interface IEventUpcaster with
        member _.Upcast(ev) =
            match ev.TypeName with
            | "OrganizationCreated" ->
                let data = ev.Data.DeepClone() :?> JObject
                data.["Name"] <- data.["FullName"]
                data.Remove("FullName") |> ignore
                { TypeName = "OrganizationRegistered"; Data = data }
            | _ -> ev

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

    let Written (typeName: string) (json: string) =
        let metadata = sprintf """{"%s":"%s"}""" KurrentDBSerialization.DiscriminatedUnionCaseTypeHeader typeName
        Resolved typeName (Encoding.UTF8.GetBytes json) (Encoding.UTF8.GetBytes metadata)

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
