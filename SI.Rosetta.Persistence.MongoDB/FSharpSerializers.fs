namespace SI.Rosetta.Persistence.MongoDB

open System
open System.Collections.Concurrent
open MongoDB.Bson
open MongoDB.Bson.IO
open MongoDB.Bson.Serialization
open MongoDB.Bson.Serialization.Serializers
open Microsoft.FSharp.Reflection

/// An F# list as a BSON array (the shape the driver already wrote), read back as an F# list
type FSharpListSerializer<'T>() =
    inherit SerializerBase<'T list>()
    let ItemSerializer = lazy (BsonSerializer.LookupSerializer<'T>())

    override this.Serialize(context, _args, value) =
        let writer = context.Writer
        writer.WriteStartArray()
        for item in (if isNull (box value) then [] else value) do
            ItemSerializer.Value.Serialize(context, item)
        writer.WriteEndArray()

    override this.Deserialize(context, _args) =
        let reader = context.Reader
        match reader.GetCurrentBsonType() with
        | BsonType.Null ->
            reader.ReadNull()
            []
        | _ ->
            let items = ResizeArray<'T>()
            reader.ReadStartArray()
            while reader.ReadBsonType() <> BsonType.EndOfDocument do
                items.Add(ItemSerializer.Value.Deserialize(context))
            reader.ReadEndArray()
            List.ofSeq items

    interface IBsonArraySerializer with
        member this.TryGetItemSerializationInfo(serializationInfo) =
            serializationInfo <- BsonSerializationInfo(null, ItemSerializer.Value, typeof<'T>)
            true

/// An F# Map: with string keys a sub-document (the shape the driver already wrote), with other keys an array of
/// [key, value] pairs. Either is read
type FSharpMapSerializer<'K, 'V when 'K : comparison>() =
    inherit SerializerBase<Map<'K, 'V>>()
    let KeySerializer = lazy (BsonSerializer.LookupSerializer<'K>())
    let ValueSerializer = lazy (BsonSerializer.LookupSerializer<'V>())
    let StringKeys = typeof<'K> = typeof<string>

    override this.Serialize(context, _args, value) =
        let writer = context.Writer
        let entries = if isNull (box value) then Map.empty else value
        if StringKeys then
            writer.WriteStartDocument()
            for KeyValue(key, item) in entries do
                writer.WriteName(unbox<string> (box key))
                ValueSerializer.Value.Serialize(context, item)
            writer.WriteEndDocument()
        else
            writer.WriteStartArray()
            for KeyValue(key, item) in entries do
                writer.WriteStartArray()
                KeySerializer.Value.Serialize(context, key)
                ValueSerializer.Value.Serialize(context, item)
                writer.WriteEndArray()
            writer.WriteEndArray()

    override this.Deserialize(context, _args) =
        let reader = context.Reader
        match reader.GetCurrentBsonType() with
        | BsonType.Null ->
            reader.ReadNull()
            Map.empty
        | BsonType.Document ->
            let entries = ResizeArray<'K * 'V>()
            reader.ReadStartDocument()
            while reader.ReadBsonType() <> BsonType.EndOfDocument do
                let name = reader.ReadName()
                let key = if StringKeys then unbox<'K> (box name) else unbox<'K> (Convert.ChangeType(name, typeof<'K>))
                entries.Add(key, ValueSerializer.Value.Deserialize(context))
            reader.ReadEndDocument()
            Map.ofSeq entries
        | _ ->
            let entries = ResizeArray<'K * 'V>()
            reader.ReadStartArray()
            while reader.ReadBsonType() <> BsonType.EndOfDocument do
                reader.ReadStartArray()
                reader.ReadBsonType() |> ignore
                let key = KeySerializer.Value.Deserialize(context)
                reader.ReadBsonType() |> ignore
                let item = ValueSerializer.Value.Deserialize(context)
                reader.ReadEndArray()
                entries.Add(key, item)
            reader.ReadEndArray()
            Map.ofSeq entries

/// An F# union as { case: "Crate", fields: [2, 3] }, Rosetta's event shape camelCased (a case without fields has no
/// fields). It also reads what the driver wrote before: { _t: "Crate" } or { _t: "_Small" }, and {} for a union
/// whose cases have no fields, which had lost their fields or case. Missing fields read as default values, and a
/// missing case as the first one, as they were read then
type FSharpUnionSerializer<'T>() =
    inherit SerializerBase<'T>()
    static let Cases = FSharpType.GetUnionCases(typeof<'T>, true)
    static let ReadTag = FSharpValue.PreComputeUnionTagReader(typeof<'T>, true)
    static let Readers = Cases |> Array.map (fun case -> FSharpValue.PreComputeUnionReader(case, true))
    static let Constructors = Cases |> Array.map (fun case -> FSharpValue.PreComputeUnionConstructor(case, true))
    static let FieldTypes = Cases |> Array.map (fun case -> case.GetFields() |> Array.map (fun field -> field.PropertyType))
    // Looked up when first needed: a recursive union's fields include itself
    static let FieldSerializers = ConcurrentDictionary<Type, IBsonSerializer>()
    static let SerializerOf (t: Type) = FieldSerializers.GetOrAdd(t, fun t -> BsonSerializer.LookupSerializer(t))

    static let CaseNamed (name: string) =
        Cases
        |> Array.tryFind (fun case -> case.Name = name || "_" + case.Name = name)
        |> Option.defaultWith (fun () ->
            raise (FormatException(sprintf "%s has no case %s." typeof<'T>.Name name)))

    override this.Serialize(context, _args, value) =
        let writer = context.Writer
        if isNull (box value) then
            writer.WriteNull()
        else
            let tag = ReadTag (box value)
            let case = Cases.[tag]
            writer.WriteStartDocument()
            writer.WriteString("case", case.Name)
            let values = Readers.[tag] (box value)
            if values.Length > 0 then
                writer.WriteStartArray("fields")
                values |> Array.iteri (fun i field ->
                    let fieldType = FieldTypes.[tag].[i]
                    (SerializerOf fieldType).Serialize(context, field))
                writer.WriteEndArray()
            writer.WriteEndDocument()

    override this.Deserialize(context, _args) =
        let reader = context.Reader
        match reader.GetCurrentBsonType() with
        | BsonType.Null ->
            reader.ReadNull()
            Unchecked.defaultof<'T>
        | _ ->
            let mutable case = Cases.[0]
            let mutable fields: obj[] = null
            reader.ReadStartDocument()
            while reader.ReadBsonType() <> BsonType.EndOfDocument do
                match reader.ReadName() with
                | "case" | "_t" -> case <- CaseNamed (reader.ReadString())
                | "fields" ->
                    // The case comes first, as Serialize writes it
                    let fieldTypes = FieldTypes.[case.Tag]
                    let values = ResizeArray<obj>()
                    reader.ReadStartArray()
                    while reader.ReadBsonType() <> BsonType.EndOfDocument do
                        let fieldType = if values.Count < fieldTypes.Length then fieldTypes.[values.Count] else typeof<obj>
                        values.Add((SerializerOf fieldType).Deserialize(context))
                    reader.ReadEndArray()
                    fields <- values.ToArray()
                | _ -> reader.SkipValue()
            reader.ReadEndDocument()
            let fieldTypes = FieldTypes.[case.Tag]
            let values =
                fieldTypes |> Array.mapi (fun i fieldType ->
                    if not (isNull fields) && i < fields.Length then fields.[i]
                    elif fieldType.IsValueType then Activator.CreateInstance(fieldType)
                    else null)
            Constructors.[case.Tag] values :?> 'T

/// Rosetta's serializers for F# lists, maps and unions. Options and sets aren't touched: the driver round-trips them
type FSharpSerializationProvider() =
    let IsGeneric (definition: Type) (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = definition

    let IsUnion (t: Type) =
        FSharpType.IsUnion(t, true)
        // The union itself, not one of its compiled case classes
        && not (not (isNull t.BaseType) && FSharpType.IsUnion(t.BaseType, true))
        && not (IsGeneric typedefof<_ list> t || IsGeneric typedefof<_ option> t || IsGeneric typedefof<_ voption> t)

    let Create (serializerDefinition: Type) (arguments: Type[]) =
        Activator.CreateInstance(serializerDefinition.MakeGenericType(arguments)) :?> IBsonSerializer

    interface IBsonSerializationProvider with
        member this.GetSerializer(t: Type) =
            if IsGeneric typedefof<_ list> t then Create typedefof<FSharpListSerializer<_>> (t.GetGenericArguments())
            elif IsGeneric typedefof<Map<_, _>> t then Create typedefof<FSharpMapSerializer<_, _>> (t.GetGenericArguments())
            elif IsUnion t then Create typedefof<FSharpUnionSerializer<_>> [| t |]
            else null
