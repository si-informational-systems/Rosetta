namespace SI.Rosetta.Common

open System.IO
open Microsoft.FSharp.Reflection
open Newtonsoft.Json
open Newtonsoft.Json.Converters
open Newtonsoft.Json.Linq

/// How an event is stored, whatever the store: the name of its DU case's record type, and that record as JSON.
/// Reading it back goes through the upcasters. KurrentDBSerialization and the Upcasting TestKit both use it
module EventEncoding =
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
