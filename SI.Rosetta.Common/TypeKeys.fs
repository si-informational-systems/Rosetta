namespace SI.Rosetta.Common

open System
open System.ComponentModel.DataAnnotations
open System.Reflection
open Microsoft.FSharp.Reflection

/// The properties a type is stored by, and its key among them: its [<Key>] properties, in the order they're declared,
/// or its Id. For read models (ProjectionKeys) and the state of aggregates stored in PostgreSQL. A key of several
/// properties is given as a tuple of their values, in that order
module TypeKeys =
    let private IsKey (property: PropertyInfo) =
        not (isNull (property.GetCustomAttribute<KeyAttribute>()))
        // On an F# record field, the attribute may be on the backing field
        || (match property.DeclaringType.GetField(property.Name + "@", BindingFlags.Instance ||| BindingFlags.NonPublic) with
            | null -> false
            | field -> not (isNull (field.GetCustomAttribute<KeyAttribute>())))

    /// The properties in declaration order: an F# record's fields, or the public instance properties
    let PropertiesOf (storedType: Type) : PropertyInfo[] =
        if FSharpType.IsRecord(storedType, true) then FSharpType.GetRecordFields(storedType, true)
        else storedType.GetProperties(BindingFlags.Instance ||| BindingFlags.Public) |> Array.sortBy (fun property -> property.MetadataToken)

    /// The key properties, in declaration order
    let KeyPropertiesOf (storedType: Type) : PropertyInfo[] =
        let properties = PropertiesOf storedType
        match properties |> Array.filter IsKey with
        | [||] ->
            match properties |> Array.tryFind (fun property -> property.Name = "Id") with
            | Some id -> [| id |]
            | None ->
                raise (InvalidOperationException(
                    sprintf "%s has no key: give it an Id, or mark its key properties with [<Key>]." storedType.Name))
        | keys -> keys

    /// The value's key values
    let KeyOf (stored: obj) : obj[] =
        KeyPropertiesOf (stored.GetType()) |> Array.map (fun property -> property.GetValue stored)

    /// The key values an id gives: a tuple's items, or the id itself
    let ValuesOf (id: obj) : obj[] =
        match id with
        | null -> raise (ArgumentNullException(nameof id))
        | id when FSharpType.IsTuple(id.GetType()) -> FSharpValue.GetTupleFields id
        | id -> [| id |]
