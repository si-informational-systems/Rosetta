namespace SI.Rosetta.Common

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection

/// How an event is stored, whatever the store: the name of its DU case's record ([<EventType>] or the type name), and that record as JSON.
/// Reading it back goes through the upcasters. KurrentDBSerialization and the Upcasting TestKit both use it
module EventEncoding =
    /// System.Text.Json. F# options are written in the given format and read in either. DUs, value options, lists,
    /// sets and maps keep the shapes Newtonsoft.Json gave them (Rosetta 1.25 and earlier), and records and tuples are
    /// left to System.Text.Json, whose shapes match, so the events already stored read the same. Reading is as
    /// lenient as Newtonsoft's: property names in any case, and numbers in strings
    let private OptionsWriting (optionFormat: OptionFormat) =
        let options =
            JsonFSharpOptions
                .NewtonsoftLike()
                .WithTypes(JsonFSharpTypes.Unions ||| JsonFSharpTypes.ValueOptions
                           ||| JsonFSharpTypes.Lists ||| JsonFSharpTypes.Sets ||| JsonFSharpTypes.Maps)
                .WithMapFormat(MapFormat.Object)
                .ToJsonSerializerOptions()
        options.Converters.Insert(0, OptionConverterFactory(optionFormat))
        options.Converters.Add(JsonStringEnumConverter())
        options.PropertyNameCaseInsensitive <- true
        options.NumberHandling <- JsonNumberHandling.AllowReadingFromString
        options.MakeReadOnly(populateMissingResolver = true)
        options

    let private PlainOptions = OptionsWriting OptionFormat.Plain
    let private NewtonsoftOptions = OptionsWriting OptionFormat.Newtonsoft

    /// The options events are read with (F# options in either format) and written with by default
    let JsonOptions = PlainOptions

    /// How Encode writes F# options: Plain (the default) for System.Text.Json and C# readers, or Newtonsoft, as
    /// Rosetta 1.25 and earlier did. Set Newtonsoft at startup while services still on 1.25 read your events: they
    /// can't read Plain options. Rosetta 1.26 reads both
    let mutable WriteOptionsAs = OptionFormat.Plain

    let private TypeNames = ConcurrentDictionary<Type, string>()

    /// The name an event record is stored under: its [<EventType>] name, or else its type name
    let TypeNameOf (recordType: Type) =
        TypeNames.GetOrAdd(recordType, fun t ->
            match t.GetCustomAttribute<EventType>(false) with
            | null -> t.Name
            | eventType -> eventType.Name)

    let private Cases = ConcurrentDictionary<Type, IDictionary<string, UnionCaseInfo>>()

    /// The events DU's cases by the name each one's record is stored under
    let private CasesOf (eventsType: Type) =
        Cases.GetOrAdd(eventsType, fun t ->
            let cases = FSharpType.GetUnionCases t |> Array.map (fun case -> TypeNameOf (GetUnionCaseType case), case)
            // Events stored under one name couldn't be told apart when they're read
            match cases |> Array.countBy fst |> Array.filter (fun (_, count) -> count > 1) with
            | [||] -> dict cases
            | clashes ->
                let names = clashes |> Array.map fst |> String.concat ", "
                raise (InvalidOperationException(
                    sprintf "%s has more than one case stored under the same event type name: %s. Give their records different [<EventType>] names." t.Name names)))

    /// The name the event's record is stored under, and the record as JSON, with F# options in the given format
    let EncodeWith (optionFormat: OptionFormat) (event: IAggregateEvents) : string * string =
        let case, fields = FSharpValue.GetUnionFields(event, event.GetType())
        let recordType = GetUnionCaseType case
        let options = if optionFormat = OptionFormat.Newtonsoft then NewtonsoftOptions else PlainOptions
        TypeNameOf recordType, JsonSerializer.Serialize(fields.[0], recordType, options)

    /// The name the event's record is stored under, and the record as JSON, with F# options as WriteOptionsAs says
    let Encode (event: IAggregateEvents) : string * string =
        EncodeWith WriteOptionsAs event

    /// The eventsType case for a stored event, after the upcasters, or the event type name eventsType has no case for
    let DecodeAs (eventsType: Type)
        (upcasters: IEventUpcaster seq) (typeName: string) (json: string) : Result<IAggregateEvents, string> =
        let FindCase name =
            match (CasesOf eventsType).TryGetValue name with
            | true, case -> Some case
            | false, _ -> None

        let MakeEvent (case: UnionCaseInfo) (record: obj) =
            FSharpValue.MakeUnion(case, [| record |]) :?> IAggregateEvents

        if Seq.isEmpty upcasters then
            match FindCase typeName with
            | Some case -> Ok (MakeEvent case (JsonSerializer.Deserialize(json, GetUnionCaseType case, JsonOptions)))
            | None -> Error typeName
        else
            let stored =
                upcasters
                |> Seq.fold (fun ev (upcaster: IEventUpcaster) -> upcaster.Upcast ev) { TypeName = typeName; Data = JsonNode.Parse(json).AsObject() }
            match FindCase stored.TypeName with
            | Some case -> Ok (MakeEvent case (stored.Data.Deserialize(GetUnionCaseType case, JsonOptions)))
            | None -> Error stored.TypeName

    /// The 'TEvent case for a stored event, after the upcasters, or the event type name 'TEvent has no case for
    let Decode<'TEvent when 'TEvent :> IAggregateEvents>
        (upcasters: IEventUpcaster seq) (typeName: string) (json: string) : Result<'TEvent, string> =
        DecodeAs typeof<'TEvent> upcasters typeName json |> Result.map (fun ev -> ev :?> 'TEvent)
