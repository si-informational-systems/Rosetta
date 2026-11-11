namespace SI.Rosetta.Projections.PostgreSQL

open System
open System.Collections
open System.Collections.Concurrent
open System.ComponentModel.DataAnnotations.Schema
open System.Data.Common
open System.Reflection
open System.Text.Json
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open Npgsql
open NpgsqlTypes
open SI.Rosetta.Common
open SI.Rosetta.Projections

/// How a property's values go to its column and come back
[<RequireQualifiedAccess>]
type internal ColumnKind =
    /// As Npgsql maps it: text, int, bigint, numeric, boolean, double, uuid, date, timestamptz (UTC)…
    | Value of Type
    /// An enum, or a union whose cases have no fields, by its name
    | Text of Type
    /// An array or F# list of plain values: text[], int[]…
    | Array of Element: Type * IsList: bool
    /// Anything else (records, unions with fields, maps), as Rosetta writes events
    | Json of Type
    /// An option: the inner value, or NULL for None
    | Option of OptionType: Type * Inner: ColumnKind

type internal PostgreSqlColumn = { Name: string; Property: PropertyInfo; Kind: ColumnKind }

/// A read model type's table: its columns, its key, and the SQL to upsert, load and delete its rows
type internal PostgreSqlTable =
    { Type: Type
      Schema: string
      Name: string
      Columns: PostgreSqlColumn[]
      Keys: PostgreSqlColumn[]
      Construct: obj[] -> obj }

    member this.Qualified = PostgreSqlTable.Quote this.Schema + "." + PostgreSqlTable.Quote this.Name

    static member Quote(identifier: string) = "\"" + identifier.Replace("\"", "\"\"") + "\""

/// The mapping from read model types to tables, and the conversions between their values and the columns'
module internal PostgreSqlTables =
    let private PlainTypes =
        Collections.Generic.HashSet<Type>(
            [| typeof<string>; typeof<int16>; typeof<int>; typeof<int64>; typeof<single>; typeof<double>; typeof<decimal>
               typeof<bool>; typeof<Guid>; typeof<DateTime>; typeof<DateTimeOffset>; typeof<DateOnly>; typeof<TimeOnly>
               typeof<TimeSpan>; typeof<byte[]> |])

    let private GetFieldValue =
        typeof<DbDataReader>.GetMethods()
        |> Array.find (fun m -> m.Name = "GetFieldValue" && m.IsGenericMethodDefinition && m.GetParameters().Length = 1)
    let private Readers = ConcurrentDictionary<Type, MethodInfo>()
    let private ListOfArray =
        typedefof<list<_>>.Assembly.GetType("Microsoft.FSharp.Collections.ListModule").GetMethod("OfArray")
    let private ListsOf = ConcurrentDictionary<Type, MethodInfo>()

    /// AreaHour → area_hour, HTTPServer → http_server
    let SnakeCase (name: string) =
        let words = Regex.Replace(name, "([A-Z]+)([A-Z][a-z])", "$1_$2")
        Regex.Replace(words, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant()

    let private IsOption (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>
    let private IsList (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>

    let private IsFieldlessUnion (t: Type) =
        FSharpType.IsUnion(t, true) && not (IsOption t) && not (IsList t)
        && FSharpType.GetUnionCases(t, true) |> Array.forall (fun case -> case.GetFields().Length = 0)

    let rec private KindOf (t: Type) : ColumnKind =
        if IsOption t then ColumnKind.Option(t, KindOf (t.GetGenericArguments().[0]))
        elif PlainTypes.Contains t then ColumnKind.Value t
        elif t.IsEnum || IsFieldlessUnion t then ColumnKind.Text t
        elif t.IsArray && PlainTypes.Contains(t.GetElementType()) && t <> typeof<byte[]> then ColumnKind.Array(t.GetElementType(), false)
        elif IsList t && PlainTypes.Contains(t.GetGenericArguments().[0]) then ColumnKind.Array(t.GetGenericArguments().[0], true)
        else ColumnKind.Json t

    let private Attribute<'A when 'A :> Attribute and 'A : null> (property: PropertyInfo) : 'A option =
        match property.GetCustomAttribute<'A>() with
        | null ->
            // On an F# record field, the attribute may be on the backing field
            match property.DeclaringType.GetField(property.Name + "@", BindingFlags.Instance ||| BindingFlags.NonPublic) with
            | null -> None
            | field -> Option.ofObj (field.GetCustomAttribute<'A>())
        | attribute -> Some attribute

    let private ColumnOf (property: PropertyInfo) =
        let name =
            match Attribute<ColumnAttribute> property with
            | Some column when not (String.IsNullOrEmpty column.Name) -> column.Name
            | _ -> SnakeCase property.Name
        { Name = name; Property = property; Kind = KindOf property.PropertyType }

    let private Constructor (t: Type) (properties: PropertyInfo[]) : obj[] -> obj =
        if FSharpType.IsRecord(t, true) then
            FSharpValue.PreComputeRecordConstructor(t, true)
        else
            fun values ->
                let readModel = Activator.CreateInstance t
                Array.iter2 (fun (property: PropertyInfo) value -> property.SetValue(readModel, value)) properties values
                readModel

    let private Tables = ConcurrentDictionary<struct (Type * string), PostgreSqlTable>()

    /// The read model type's table: [<Table>] names it, else the snake_case plural of the type's name, in the
    /// [<Table>]'s schema or else the configured one
    let Of (defaultSchema: string) (readModelType: Type) : PostgreSqlTable =
        Tables.GetOrAdd(struct (readModelType, defaultSchema), fun _ ->
            let properties =
                ProjectionKeys.PropertiesOf readModelType
                |> Array.filter (fun property -> FSharpType.IsRecord(readModelType, true) || property.CanWrite)
            let columns = properties |> Array.map ColumnOf
            let keys =
                ProjectionKeys.KeyPropertiesOf readModelType
                |> Array.map (fun key -> columns |> Array.find (fun column -> column.Property.Name = key.Name))
            let table = readModelType.GetCustomAttribute<TableAttribute>() |> Option.ofObj
            { Type = readModelType
              Schema =
                match table with
                | Some table when not (String.IsNullOrEmpty table.Schema) -> table.Schema
                | _ -> defaultSchema
              Name =
                match table with
                | Some table when not (String.IsNullOrEmpty table.Name) -> table.Name
                | _ -> SnakeCase (PluralizeName readModelType.Name)
              Columns = columns
              Keys = keys
              Construct = Constructor readModelType properties })

    let private Utc (at: DateTime) =
        match at.Kind with
        | DateTimeKind.Utc -> at
        | DateTimeKind.Local -> at.ToUniversalTime()
        | _ -> DateTime.SpecifyKind(at, DateTimeKind.Utc)

    /// The value as its column takes it
    let rec ToColumn (kind: ColumnKind) (value: obj) : obj =
        match value with
        | null -> box DBNull.Value
        | value ->
            match kind with
            | ColumnKind.Value _ ->
                match value with
                | :? DateTime as at -> box (Utc at)
                | :? DateTimeOffset as at -> box (at.ToUniversalTime())
                | value -> value
            | ColumnKind.Text t ->
                if t.IsEnum then box (value.ToString())
                else box (fst (FSharpValue.GetUnionFields(value, t, true))).Name
            | ColumnKind.Array(element, _) ->
                let items = [| for item in (value :?> IEnumerable) -> ToColumn (ColumnKind.Value element) item |]
                let array = Array.CreateInstance(element, items.Length)
                items |> Array.iteri (fun i item -> array.SetValue(item, i))
                box array
            | ColumnKind.Json t -> box (JsonSerializer.Serialize(value, t, EventEncoding.JsonOptions))
            | ColumnKind.Option(optionType, inner) ->
                ToColumn inner (optionType.GetProperty("Value").GetValue value)

    let Parameter (kind: ColumnKind) (value: obj) =
        let parameter = NpgsqlParameter(Value = ToColumn kind value)
        match kind with
        | ColumnKind.Json _
        | ColumnKind.Option(_, ColumnKind.Json _) -> parameter.NpgsqlDbType <- NpgsqlDbType.Jsonb
        | _ -> ()
        parameter

    let private Read (t: Type) (reader: DbDataReader) (ordinal: int) =
        Readers.GetOrAdd(t, fun t -> GetFieldValue.MakeGenericMethod t).Invoke(reader, [| box ordinal |])

    /// The column's value as the property takes it
    let rec FromColumn (kind: ColumnKind) (reader: DbDataReader) (ordinal: int) : obj =
        if reader.IsDBNull ordinal then
            match kind with
            | ColumnKind.Value t
            | ColumnKind.Text t when t.IsValueType -> Activator.CreateInstance t
            | _ -> null
        else
            match kind with
            | ColumnKind.Value t -> Read t reader ordinal
            | ColumnKind.Text t ->
                let name = reader.GetString ordinal
                if t.IsEnum then Enum.Parse(t, name)
                else
                    match FSharpType.GetUnionCases(t, true) |> Array.tryFind (fun case -> case.Name = name) with
                    | Some case -> FSharpValue.MakeUnion(case, [||], true)
                    | None -> raise (InvalidOperationException(sprintf "%s has no case %s." t.Name name))
            | ColumnKind.Array(element, isList) ->
                let array = Read (element.MakeArrayType()) reader ordinal
                if isList then ListsOf.GetOrAdd(element, fun element -> ListOfArray.MakeGenericMethod element).Invoke(null, [| array |])
                else array
            | ColumnKind.Json t -> JsonSerializer.Deserialize(reader.GetString ordinal, t, EventEncoding.JsonOptions)
            | ColumnKind.Option(optionType, inner) ->
                let some = FSharpType.GetUnionCases(optionType, true) |> Array.find (fun case -> case.Name = "Some")
                FSharpValue.MakeUnion(some, [| FromColumn inner reader ordinal |], true)

    /// The row, read from the reader's current row (its columns in the table's order)
    let ReadRow (table: PostgreSqlTable) (reader: DbDataReader) =
        table.Construct(table.Columns |> Array.mapi (fun ordinal column -> FromColumn column.Kind reader ordinal))

    /// A key, as its columns take it, to compare a requested key with a row's
    let KeyText (table: PostgreSqlTable) (values: obj[]) =
        if values.Length <> table.Keys.Length then
            raise (ArgumentException(sprintf "%s's key has %d values (%s), but %d were given." table.Type.Name table.Keys.Length
                                         (String.Join(", ", table.Keys |> Array.map (fun key -> key.Property.Name))) values.Length))
        String.Join("\u001F", Array.map2 (fun (key: PostgreSqlColumn) value -> string (ToColumn key.Kind value)) table.Keys values)

    let private ColumnList (table: PostgreSqlTable) =
        String.Join(", ", table.Columns |> Array.map (fun column -> PostgreSqlTable.Quote column.Name))

    /// insert … on conflict (key) do update: the whole row
    let Upsert (table: PostgreSqlTable) (row: obj) =
        let values = table.Columns |> Array.map (fun column -> Parameter column.Kind (column.Property.GetValue row))
        let placeholders = String.Join(", ", values |> Array.mapi (fun i _ -> sprintf "$%d" (i + 1)))
        let keys = String.Join(", ", table.Keys |> Array.map (fun key -> PostgreSqlTable.Quote key.Name))
        let others = table.Columns |> Array.filter (fun column -> not (table.Keys |> Array.contains column))
        let onConflict =
            if others.Length = 0 then "do nothing"
            else "do update set " + String.Join(", ", others |> Array.map (fun column -> sprintf "%s = excluded.%s" (PostgreSqlTable.Quote column.Name) (PostgreSqlTable.Quote column.Name)))
        let command =
            NpgsqlBatchCommand(sprintf "insert into %s (%s) values (%s) on conflict (%s) %s" table.Qualified (ColumnList table) placeholders keys onConflict)
        command.Parameters.AddRange values
        command

    /// where (key = …) [or (key = …)…], with its parameters
    let private WhereKeys (table: PostgreSqlTable) (ids: obj[]) =
        let parameters = Collections.Generic.List<NpgsqlParameter>()
        let condition (values: obj[]) =
            let equals =
                Array.map2 (fun (key: PostgreSqlColumn) value ->
                    parameters.Add(Parameter key.Kind value)
                    sprintf "%s = $%d" (PostgreSqlTable.Quote key.Name) parameters.Count) table.Keys values
            "(" + String.Join(" and ", equals) + ")"
        let conditions =
            ids |> Array.map (fun id ->
                let values = ProjectionKeys.ValuesOf id
                KeyText table values |> ignore
                condition values)
        String.Join(" or ", conditions), parameters.ToArray()

    let Delete (table: PostgreSqlTable) (ids: obj[]) =
        let where, parameters = WhereKeys table ids
        let command = NpgsqlBatchCommand(sprintf "delete from %s where %s" table.Qualified where)
        command.Parameters.AddRange parameters
        command

    /// The rows with these keys
    let Select (table: PostgreSqlTable) (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) (ids: obj[]) =
        let where, parameters = WhereKeys table ids
        let command = new NpgsqlCommand(sprintf "select %s from %s where %s" (ColumnList table) table.Qualified where, connection, transaction)
        command.Parameters.AddRange parameters
        command
