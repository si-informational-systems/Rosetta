namespace SI.Rosetta.Projections

open System
open System.Reflection
open SI.Rosetta.Common

/// Declares a read model type the projection writes: its table (PostgreSQL) or collection (RavenDB, MongoDB). Rosetta
/// checks a PostgreSQL table against it when the projection starts, and empties it when the projection is rebuilt
/// ([<ProjectionVersion>]). A read model type belongs to one projection
type IProjectInto<'TReadModel> = interface end

/// The read model types a projection declares with IProjectInto
module ProjectionReadModels =
    let Of (projectionType: Type) : Type[] =
        projectionType.GetInterfaces()
        |> Array.filter (fun i -> i.IsGenericType && i.GetGenericTypeDefinition() = typedefof<IProjectInto<_>>)
        |> Array.map (fun i -> i.GetGenericArguments().[0])

    /// Fails when two of the projections declare the same read model: rebuilding a projection empties its read models
    let CheckOneProjectionEach (projectionTypes: Type seq) =
        let shared =
            projectionTypes
            |> Seq.collect (fun projectionType -> Of projectionType |> Seq.map (fun readModel -> readModel, projectionType))
            |> Seq.groupBy fst
            |> Seq.filter (fun (_, owners) -> Seq.length owners > 1)
            |> Seq.toList
        match shared with
        | [] -> ()
        | (readModel, owners) :: _ ->
            raise (InvalidOperationException(
                sprintf "%s is a read model of %s: a read model belongs to one projection (IProjectInto), as rebuilding a projection empties its read models."
                    readModel.Name (String.Join(" and ", owners |> Seq.map (fun (_, owner) -> owner.Name)))))

    /// Why the projection can be rebuilt, if it can: a [<ProjectionVersion>] above 0, or a custom projection's cp-
    /// stream, which is linked again when the custom projection's definition changes
    let RebuildReasonOf (projectionType: Type) : string option =
        let version = projectionType.GetCustomAttribute<ProjectionVersion>() |> box |> Option.ofObj |> Option.map (fun attribute -> (attribute :?> ProjectionVersion).Version)
        let stream = projectionType.GetCustomAttribute<HandlesStream>() |> box |> Option.ofObj |> Option.map (fun attribute -> (attribute :?> HandlesStream).Name)
        match version, stream with
        | Some version, _ when version > 0 -> Some (sprintf "it has [<ProjectionVersion(%d)>]" version)
        | _, Some stream when not (isNull stream) && stream.StartsWith "cp-" ->
            Some (sprintf "it reads %s, which is linked again when its custom projection's definition changes" stream)
        | _ -> None

    /// Fails, naming why, when the projection can be rebuilt but has nothing to empty first: replaying its stream over
    /// its read models would apply every event twice. Unless it's marked [<RebuildWithoutClearing>]
    let RequireClearable (projectionType: Type) (reason: string) =
        if (Of projectionType).Length = 0 && not (Attribute.IsDefined(projectionType, typeof<RebuildWithoutClearing>)) then
            raise (InvalidOperationException(
                sprintf "Projection %s can be rebuilt (%s), but declares no read models to empty first: replaying its stream over them would apply every event twice. Declare them with IProjectInto<…>, or mark it [<RebuildWithoutClearing>] when every handler only writes whole documents or rows."
                    projectionType.Name reason))

    /// Fails for any of the projections that can be rebuilt but declares no read models to empty first
    let CheckRebuildable (projectionTypes: Type seq) =
        for projectionType in projectionTypes do
            RebuildReasonOf projectionType |> Option.iter (RequireClearable projectionType)

/// The stores a projection can write to, and which one it does: a projection whose handlers take ISqlStore writes to
/// PostgreSQL, one whose handlers take INoSqlStore to RavenDB or MongoDB. Its transactions and checkpoints are that
/// store's, registered as keyed services under its name
module ProjectionStoreKinds =
    [<Literal>]
    let Documents = "Documents"
    [<Literal>]
    let Sql = "Sql"

    let private HandlerTypesOf (projectionType: Type) =
        projectionType.GetInterfaces()
        |> Array.filter (fun i -> i.IsGenericType && i.GetGenericTypeDefinition() = typedefof<IAmHandledBy<_, _>>)
        |> Array.map (fun i -> i.GetGenericArguments().[0])

    let private Takes (store: Type) (handlerType: Type) =
        handlerType.GetConstructors()
        |> Array.exists (fun constructor -> constructor.GetParameters() |> Array.exists (fun parameter -> parameter.ParameterType = store))

    /// Sql when its handlers take ISqlStore, Documents otherwise. Both fails: one transaction can't span two databases
    let Of (projectionType: Type) =
        let handlers = HandlerTypesOf projectionType
        let sql = handlers |> Array.exists (Takes typeof<ISqlStore>)
        let documents = handlers |> Array.exists (Takes typeof<INoSqlStore>)
        match sql, documents with
        | true, true ->
            raise (InvalidOperationException(
                sprintf "Projection %s has handlers that write to PostgreSQL (ISqlStore) and to RavenDB or MongoDB (INoSqlStore): each event is one transaction, which can't span two databases. Split it into two projections." projectionType.Name))
        | true, false -> Sql
        | _ -> Documents

/// A read model's key: its [<Key>] properties, in the order they're declared, or its Id. A key of several properties is
/// given to LoadAsync, LoadManyAsync and DeleteAsync as a tuple of their values, in that order
module ProjectionKeys =
    /// The properties in declaration order: an F# record's fields, or the public instance properties
    let PropertiesOf (readModelType: Type) : PropertyInfo[] = TypeKeys.PropertiesOf readModelType

    /// The key properties, in declaration order
    let KeyPropertiesOf (readModelType: Type) : PropertyInfo[] = TypeKeys.KeyPropertiesOf readModelType

    /// The read model's key values
    let KeyOf (readModel: obj) : obj[] = TypeKeys.KeyOf readModel

    /// The key values an id gives: a tuple's items, or the id itself
    let ValuesOf (id: obj) : obj[] = TypeKeys.ValuesOf id
