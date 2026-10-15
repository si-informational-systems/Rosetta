namespace SI.Rosetta.Serialization

open SI.Rosetta.Common

/// StoredEvents' encoding, as KurrentDB events: the record JSON is the event data, and the metadata names the record type
module KurrentDBSerialization =
    open Newtonsoft.Json
    open System.Collections.Generic
    open System.Text
    open Microsoft.FSharp.Reflection
    open KurrentDB.Client

    let AggregateClrTypeNameHeader = "AggregateClrTypeName"
    let EventClrTypeNameHeader = "EventClrTypeName"
    let EventsDUClrTypeNameHeader = "EventsDiscriminatedUnionClrTypeName"
    let DiscriminatedUnionCaseTypeHeader = "DiscriminatedUnionCaseType"

    let JsonSettings = StoredEvents.JsonSettings

    type KurrentDBSerializedEvent = {
        EventClrName: string
        Data: byte[]
        Metadata: byte[]
    }

    let Serialize(event: IAggregateEvents, headers: IDictionary<string, obj>) : KurrentDBSerializedEvent =
        let typeName, json = StoredEvents.Encode event
        let data = Encoding.UTF8.GetBytes json

        let case, _ = FSharpValue.GetUnionFields(event, event.GetType())
        let fieldType = GetUnionCaseType case

        let eventHeaders =
            let dict = Dictionary<string, obj> headers
            dict.Add(EventClrTypeNameHeader, fieldType.AssemblyQualifiedName)
            dict.Add(EventsDUClrTypeNameHeader, event.GetType().AssemblyQualifiedName)
            dict.Add(DiscriminatedUnionCaseTypeHeader, typeName)
            dict
        let metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(eventHeaders, JsonSettings))

        let serialized: KurrentDBSerializedEvent = {
            EventClrName = typeName
            Data = data
            Metadata = metadata
        }
        serialized

    /// The event as the 'TEvent case its record type names, after the upcasters, or the record type name 'TEvent has no case for
    let private ReadEvent<'TEvent when 'TEvent :> IAggregateEvents> (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : Result<'TEvent, string> =
        let metadata = resolvedEvent.Event.Metadata.ToArray()
        let metadataObj = JsonConvert.DeserializeObject<Dictionary<string, obj>>(Encoding.UTF8.GetString(metadata))
        let caseTypeName = metadataObj.[DiscriminatedUnionCaseTypeHeader].ToString()
        let json = Encoding.UTF8.GetString(resolvedEvent.Event.Data.ToArray())
        StoredEvents.Decode<'TEvent> upcasters caseTypeName json

    /// The event as a 'TEvent case, after the upcasters. Throws UnknownEventException when 'TEvent has no case for its record type
    let DeserializeWith<'TEvent when 'TEvent :> IAggregateEvents> (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : 'TEvent =
        match ReadEvent<'TEvent> upcasters resolvedEvent with
        | Ok ev -> ev
        | Error typeName ->
            raise (UnknownEventException(
                typeName, typeof<'TEvent>, resolvedEvent.Event.EventStreamId, resolvedEvent.Event.EventNumber.ToUInt64()))

    /// The event as a 'TEvent case, after the upcasters, or None when 'TEvent has no case for its record type
    let TryDeserializeWith<'TEvent when 'TEvent :> IAggregateEvents> (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : 'TEvent option =
        ReadEvent<'TEvent> upcasters resolvedEvent |> Result.toOption

    /// The event as a 'TEvent case. Throws UnknownEventException when 'TEvent has no case for its record type
    let Deserialize<'TEvent when 'TEvent :> IAggregateEvents>(resolvedEvent: ResolvedEvent) : 'TEvent =
        DeserializeWith<'TEvent> Seq.empty resolvedEvent
