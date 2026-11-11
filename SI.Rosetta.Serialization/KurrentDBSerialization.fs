namespace SI.Rosetta.Serialization

open SI.Rosetta.Common

/// EventEncoding as KurrentDB events: the event type is the record's stored name, and the record JSON is the event data.
/// Rosetta writes no metadata: KurrentDB.Client adds the command's trace ($traceId, $spanId) to an append made inside one
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
        /// The headers given, as JSON; empty when there are none
        Metadata: byte[]
    }

    /// KurrentDB.Client's names for the trace and span of the activity an event was appended in
    let TraceIdHeader = "$traceId"
    let SpanIdHeader = "$spanId"

    /// The event's stored name as its event type, its record as data, and the headers given (none, from Rosetta) as
    /// metadata. Reading an event needs only its event type
    let Serialize(event: IAggregateEvents, headers: IDictionary<string, obj>) : KurrentDBSerializedEvent =
        let typeName, json = EventEncoding.Encode event
        { EventClrName = typeName
          Data = Encoding.UTF8.GetBytes json
          Metadata = if headers.Count = 0 then Array.Empty<byte>() else JsonSerializer.SerializeToUtf8Bytes(headers, JsonOptions) }

    /// The event as the eventsType case its event type names, after the upcasters, or the stored name eventsType has
    /// no case for. The event type is the record's stored name, as every Rosetta version wrote it
    let private ReadEvent (eventsType: Type) (upcasters: IEventUpcaster seq) (resolvedEvent: ResolvedEvent) : Result<IAggregateEvents, string> =
        let json = Encoding.UTF8.GetString(resolvedEvent.Event.Data.Span)
        EventEncoding.DecodeAs eventsType upcasters resolvedEvent.Event.EventType json

    /// The trace the event was appended in, from the $traceId and $spanId KurrentDB.Client wrote in its metadata, or the
    /// default (no parent) when it has none. The client doesn't store the sampled flag: the trace is taken as sampled,
    /// so a sampler that follows its parent records the projections' spans
    let TraceContextOf (resolvedEvent: ResolvedEvent) : ActivityContext =
        if resolvedEvent.Event.Metadata.IsEmpty then ActivityContext()
        else
            try
                use metadata = JsonDocument.Parse(resolvedEvent.Event.Metadata)
                let mutable traceId = Unchecked.defaultof<JsonElement>
                let mutable spanId = Unchecked.defaultof<JsonElement>
                if metadata.RootElement.ValueKind = JsonValueKind.Object
                   && metadata.RootElement.TryGetProperty(TraceIdHeader, &traceId)
                   && metadata.RootElement.TryGetProperty(SpanIdHeader, &spanId) then
                    ActivityContext(
                        ActivityTraceId.CreateFromString(traceId.GetString()),
                        ActivitySpanId.CreateFromString(spanId.GetString()),
                        ActivityTraceFlags.Recorded,
                        null,
                        true)
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
