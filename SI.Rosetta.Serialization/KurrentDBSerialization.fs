namespace SI.Rosetta.Serialization

open SI.Rosetta.Common

/// EventEncoding as KurrentDB events: the event type is the record's stored name, the record JSON is the event data,
/// and the metadata holds only the command's trace context (nothing when there's none)
module KurrentDBSerialization =
    open System
    open System.Diagnostics
    open System.Text.Json
    open System.Collections.Generic
    open System.Text
    open KurrentDB.Client

    let JsonOptions = EventEncoding.JsonOptions

    type KurrentDBSerializedEvent = {
        /// The KurrentDB event type: the record's stored name ([<EventType>] or its type name)
        EventClrName: string
        Data: byte[]
        /// The headers as JSON; empty when there are none
        Metadata: byte[]
    }

    /// The event's stored name as its event type, its record as data, and as metadata the headers given plus the
    /// command's W3C trace context (traceparent, tracestate), which the projections handling it continue. Nothing else
    /// goes in the metadata: reading an event needs only its event type. Since Rosetta 1.38: before, the metadata also
    /// held the stored name (DiscriminatedUnionCaseType) and CLR type names, which nothing read
    let Serialize(event: IAggregateEvents, headers: IDictionary<string, obj>) : KurrentDBSerializedEvent =
        let typeName, json = EventEncoding.Encode event
        let eventHeaders = Dictionary<string, obj> headers
        match Activity.Current with
        | null -> ()
        | activity ->
            eventHeaders.[RosettaTelemetry.TraceParentHeader] <- activity.Id
            if not (String.IsNullOrEmpty activity.TraceStateString) then
                eventHeaders.[RosettaTelemetry.TraceStateHeader] <- activity.TraceStateString
        { EventClrName = typeName
          Data = Encoding.UTF8.GetBytes json
          Metadata = if eventHeaders.Count = 0 then Array.Empty<byte>() else JsonSerializer.SerializeToUtf8Bytes(eventHeaders, JsonOptions) }

    /// The event as the eventsType case its event type names, after the upcasters, or the stored name eventsType has
    /// no case for. The event type is the record's stored name, as every Rosetta version wrote it
    let private ReadEvent (eventsType: Type) (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : Result<IAggregateEvents, string> =
        let json = Encoding.UTF8.GetString(resolvedEvent.Event.Data.Span)
        EventEncoding.DecodeAs eventsType upcasters resolvedEvent.Event.EventType json

    /// The trace context of the command that stored the event, or the default (no parent) when it has none
    let TraceContextOf (resolvedEvent: ResolvedEvent) : ActivityContext =
        if resolvedEvent.Event.Metadata.IsEmpty then ActivityContext()
        else
            try
                use metadata = JsonDocument.Parse(resolvedEvent.Event.Metadata)
                let mutable traceParent = Unchecked.defaultof<JsonElement>
                let mutable traceState = Unchecked.defaultof<JsonElement>
                if metadata.RootElement.ValueKind = JsonValueKind.Object
                   && metadata.RootElement.TryGetProperty(RosettaTelemetry.TraceParentHeader, &traceParent) then
                    let state =
                        if metadata.RootElement.TryGetProperty(RosettaTelemetry.TraceStateHeader, &traceState) then traceState.GetString()
                        else null
                    let mutable context = Unchecked.defaultof<ActivityContext>
                    if ActivityContext.TryParse(traceParent.GetString(), state, &context) then context else ActivityContext()
                else ActivityContext()
            with _ -> ActivityContext()

    /// The event as an eventsType case, after the upcasters. Throws UnknownEventException when eventsType has no case for its record type
    let DeserializeAs (eventsType: Type) (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : IAggregateEvents =
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
