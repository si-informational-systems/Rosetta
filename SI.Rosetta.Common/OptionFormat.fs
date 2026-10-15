namespace SI.Rosetta.Common

open System
open System.Text.Json
open System.Text.Json.Serialization

/// How F# options are written in events. Rosetta reads both
type OptionFormat =
    /// The value itself, or null for None: what System.Text.Json and C# read (Rosetta 1.26 and later)
    | Plain = 0
    /// {"Case":"Some","Fields":[value]}, or null for None: what Rosetta 1.25 and earlier wrote and read
    | Newtonsoft = 1

/// Reads an F# option in either format, and writes it in the one it's given
type internal OptionConverter<'T>(format: OptionFormat) =
    inherit JsonConverter<'T option>()

    let IsNewtonsoftShaped (element: JsonElement) =
        let mutable case = Unchecked.defaultof<JsonElement>
        element.ValueKind = JsonValueKind.Object
        && element.TryGetProperty("Case", &case)
        && case.ValueKind = JsonValueKind.String
        && (case.GetString() = "Some" || case.GetString() = "None")

    override _.HandleNull = true

    override _.Read(reader, _typeToConvert, options) =
        use document = JsonDocument.ParseValue(&reader)
        let element = document.RootElement
        if element.ValueKind = JsonValueKind.Null then None
        elif IsNewtonsoftShaped element then
            match element.GetProperty("Case").GetString() with
            | "Some" -> Some (element.GetProperty("Fields").[0].Deserialize<'T>(options))
            | _ -> None
        else Some (element.Deserialize<'T>(options))

    override _.Write(writer, value, options) =
        match value, format with
        | None, _ -> writer.WriteNullValue()
        | Some v, OptionFormat.Newtonsoft ->
            writer.WriteStartObject()
            writer.WriteString("Case", "Some")
            writer.WriteStartArray("Fields")
            JsonSerializer.Serialize(writer, v, options)
            writer.WriteEndArray()
            writer.WriteEndObject()
        | Some v, _ -> JsonSerializer.Serialize(writer, v, options)

type internal OptionConverterFactory(format: OptionFormat) =
    inherit JsonConverterFactory()

    override _.CanConvert(typeToConvert) =
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() = typedefof<option<_>>

    override _.CreateConverter(typeToConvert, _options) =
        let converterType = typedefof<OptionConverter<_>>.MakeGenericType(typeToConvert.GetGenericArguments())
        Activator.CreateInstance(converterType, [| box format |]) :?> JsonConverter
