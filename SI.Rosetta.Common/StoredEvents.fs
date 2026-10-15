namespace SI.Rosetta.Common

open System
open System.IO
open Microsoft.FSharp.Reflection
open Newtonsoft.Json
open Newtonsoft.Json.Converters
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
/// by Aggregates and Projections (and the TestKits' Given events), goes through all of them in registration order
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

/// An event as it's stored, whatever the store: the name of its DU case's record type, and that record as JSON.
/// Reading it back goes through the upcasters
module StoredEvents =
    let JsonSettings =
        let settings = JsonSerializerSettings()
        settings.Converters.Add(new StringEnumConverter())
        settings

    let private Serializer = JsonSerializer.Create(JsonSettings)

    let private ParseData (json: string) =
        // DateParseHandling.None keeps date-like strings as written, so a string field reads back unchanged
        use reader = new JsonTextReader(new StringReader(json), DateParseHandling = DateParseHandling.None)
        JObject.Load(reader)

    /// The event's record type name, and its record as JSON
    let Encode (event: IAggregateEvents) : string * string =
        let serializedDU = JsonConvert.SerializeObject(event, JsonSettings)
        let record = JObject.Parse(serializedDU).["Fields"].[0]
        let case, _ = FSharpValue.GetUnionFields(event, event.GetType())
        (GetUnionCaseType case).Name, record.ToString()

    /// The 'TEvent case for a stored event, after the upcasters, or the record type name 'TEvent has no case for
    let Decode<'TEvent when 'TEvent :> IAggregateEvents>
        (upcasters: IEventUpcaster seq) (typeName: string) (json: string) : Result<'TEvent, string> =
        let FindCase name =
            FSharpType.GetUnionCases(typeof<'TEvent>)
            |> Array.tryFind (fun case -> (GetUnionCaseType case).Name = name)

        let MakeEvent (case: UnionCaseInfo) (record: obj) =
            FSharpValue.MakeUnion(case, [| record |]) :?> 'TEvent

        if Seq.isEmpty upcasters then
            match FindCase typeName with
            | Some case -> Ok (MakeEvent case (JsonConvert.DeserializeObject(json, GetUnionCaseType case, JsonSettings)))
            | None -> Error typeName
        else
            let stored =
                upcasters
                |> Seq.fold (fun ev (upcaster: IEventUpcaster) -> upcaster.Upcast ev) { TypeName = typeName; Data = ParseData json }
            match FindCase stored.TypeName with
            | Some case -> Ok (MakeEvent case (stored.Data.ToObject(GetUnionCaseType case, Serializer)))
            | None -> Error stored.TypeName
