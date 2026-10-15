namespace SI.Rosetta.Common

open System
open Newtonsoft.Json.Linq

/// An event as it was written, before it's matched to a case of the events DU it's read into
type StoredEvent = {
    /// The name of the record type it was written with, which picks the DU case
    TypeName: string
    /// The record, as JSON
    Data: JObject
}

/// Rewrites an event written by an older version of the domain into one the current events DU can read,
/// e.g. after a record type or one of its fields was renamed.
/// Register upcasters in DI (services.AddSingleton<IEventUpcaster, MyUpcaster>()): every event read from KurrentDB,
/// by Aggregates and Projections, goes through all of them in registration order
type IEventUpcaster =
    /// The event in its current shape, or the event unchanged when the upcaster doesn't apply to it
    abstract member Upcast: StoredEvent -> StoredEvent

/// Thrown when an event's record type has no case in the events DU it's read into
type UnknownEventException(typeName: string, eventsType: Type, streamName: string, eventNumber: uint64) =
    inherit Exception(
        sprintf "Event %s (event %d of stream %s) has no case in %s. Add the case or upcast the event with an IEventUpcaster; a projection can skip it with [<SkipUnknownEvents>]."
            typeName eventNumber streamName eventsType.Name)

    member this.TypeName = typeName
    member this.EventsType = eventsType
    member this.StreamName = streamName
    member this.EventNumber = eventNumber
