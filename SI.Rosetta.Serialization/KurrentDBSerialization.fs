namespace SI.Rosetta.Serialization

open SI.Rosetta.Common

/// EventEncoding as KurrentDB events: the record JSON is the event data, and the metadata names the record type
module KurrentDBSerialization =
    open System.Diagnostics
    open System.Text.Json
    open System.Collections.Generic
    open System.Text
    open Microsoft.FSharp.Reflection
    open KurrentDB.Client

    let AggregateClrTypeNameHeader = "AggregateClrTypeName"
    let EventClrTypeNameHeader = "EventClrTypeName"
    let EventsDUClrTypeNameHeader = "EventsDiscriminatedUnionClrTypeName"
    let DiscriminatedUnionCaseTypeHeader = "DiscriminatedUnionCaseType"

    let JsonOptions = EventEncoding.JsonOptions

    type KurrentDBSerializedEvent = {
        EventClrName: string
        Data: byte[]
        Metadata: byte[]
    }

    let Serialize(event: IAggregateEvents, headers: IDictionary<string, obj>) : KurrentDBSerializedEvent =
        let typeName, json = EventEncoding.Encode event
        let data = Encoding.UTF8.GetBytes json

        let case, _ = FSharpValue.GetUnionFields(event, event.GetType())
        let fieldType = GetUnionCaseType case

        let eventHeaders =
            let dict = Dictionary<string, obj> headers
            dict.Add(EventClrTypeNameHeader, fieldType.AssemblyQualifiedName)
            dict.Add(EventsDUClrTypeNameHeader, event.GetType().AssemblyQualifiedName)
            dict.Add(DiscriminatedUnionCaseTypeHeader, typeName)
            // The command's trace, which the projections handling the event continue
            match Activity.Current with
            | null -> ()
            | activity ->
                dict.[RosettaTelemetry.TraceParentHeader] <- activity.Id
                if not (System.String.IsNullOrEmpty activity.TraceStateString) then
                    dict.[RosettaTelemetry.TraceStateHeader] <- activity.TraceStateString
            dict
        let metadata = JsonSerializer.SerializeToUtf8Bytes(eventHeaders, JsonOptions)

        let serialized: KurrentDBSerializedEvent = {
            EventClrName = typeName
            Data = data
            Metadata = metadata
        }
        serialized

    /// The event as the eventsType case its record type names, after the upcasters, or the record type name eventsType has no case for
    let private ReadEvent (eventsType: System.Type) (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : Result<IAggregateEvents, string> =
        use metadata = JsonDocument.Parse(resolvedEvent.Event.Metadata)
        let caseTypeName = metadata.RootElement.GetProperty(DiscriminatedUnionCaseTypeHeader).GetString()
        let json = Encoding.UTF8.GetString(resolvedEvent.Event.Data.ToArray())
        EventEncoding.DecodeAs eventsType upcasters caseTypeName json

    /// The trace context of the command that stored the event, or the default (no parent) when it has none
    let TraceContextOf (resolvedEvent: ResolvedEvent) : ActivityContext =
        try
            use metadata = JsonDocument.Parse(resolvedEvent.Event.Metadata)
            let mutable traceParent = Unchecked.defaultof<JsonElement>
            let mutable traceState = Unchecked.defaultof<JsonElement>
            if metadata.RootElement.TryGetProperty(RosettaTelemetry.TraceParentHeader, &traceParent) then
                let state =
                    if metadata.RootElement.TryGetProperty(RosettaTelemetry.TraceStateHeader, &traceState) then traceState.GetString()
                    else null
                let mutable context = Unchecked.defaultof<ActivityContext>
                if ActivityContext.TryParse(traceParent.GetString(), state, &context) then context else ActivityContext()
            else ActivityContext()
        with _ -> ActivityContext()

    /// The event as an eventsType case, after the upcasters. Throws UnknownEventException when eventsType has no case for its record type
    let DeserializeAs (eventsType: System.Type) (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : IAggregateEvents =
        match ReadEvent eventsType upcasters resolvedEvent with
        | Ok ev -> ev
        | Error typeName ->
            raise (UnknownEventException(
                typeName, eventsType, resolvedEvent.Event.EventStreamId, resolvedEvent.Event.EventNumber.ToUInt64()))

    /// The event as a 'TEvent case, after the upcasters. Throws UnknownEventException when 'TEvent has no case for its record type
    let DeserializeWith<'TEvent when 'TEvent :> IAggregateEvents> (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : 'TEvent =
        DeserializeAs typeof<'TEvent> upcasters resolvedEvent :?> 'TEvent

    /// The event as a 'TEvent case, after the upcasters, or None when 'TEvent has no case for its record type
    let TryDeserializeWith<'TEvent when 'TEvent :> IAggregateEvents> (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : 'TEvent option =
        match ReadEvent typeof<'TEvent> upcasters resolvedEvent with
        | Ok ev -> Some (ev :?> 'TEvent)
        | Error _ -> None

    /// The event as a 'TEvent case. Throws UnknownEventException when 'TEvent has no case for its record type
    let Deserialize<'TEvent when 'TEvent :> IAggregateEvents>(resolvedEvent: ResolvedEvent) : 'TEvent =
        DeserializeWith<'TEvent> Seq.empty resolvedEvent
