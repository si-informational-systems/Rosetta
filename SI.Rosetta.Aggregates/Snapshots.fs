namespace SI.Rosetta.Aggregates

open System
open System.Collections.Concurrent
open System.Reflection
open System.Security.Cryptography
open System.Text
open System.Text.Json
open SI.Rosetta.Common

/// Snapshots an event-sourced aggregate's state every Every events, so loading it reads the latest snapshot and only
/// the events after it, instead of its whole stream. For long-lived aggregates only: a short stream loads as fast
/// without. The state must round-trip as JSON: public properties with public setters, no other fields (checked when
/// it's first loaded). A snapshot is ignored, and the stream read in full, when the state's properties changed since
/// it was taken, or when Version is raised: raise it when ApplyEvent changes what it builds from the same events
[<AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)>]
type SnapshotAttribute(every: int) =
    inherit Attribute()
    do if every < 1 then raise (ArgumentOutOfRangeException(nameof every, every, "A snapshot is taken every 1 event or more."))
    /// A snapshot is taken each time the aggregate's Version reaches a multiple of it
    member _.Every = every
    /// Raised to ignore the snapshots taken before
    member val Version = 0 with get, set

/// What every store's snapshots share: whether and when a state is snapshotted, the fingerprint of its shape, and its
/// JSON (as events are written: EventEncoding.JsonOptions)
module Snapshots =
    let private Checked = ConcurrentDictionary<Type, string>()

    /// A type's name for the fingerprint: its namespace, name and type arguments, without the assembly's version
    let rec private NameOf (t: Type) =
        if t.IsGenericType then
            sprintf "%s.%s<%s>" t.Namespace t.Name (String.Join(",", t.GetGenericArguments() |> Array.map NameOf))
        elif t.IsArray then NameOf (t.GetElementType()) + "[]"
        else sprintf "%s.%s" t.Namespace t.Name

    /// The types of the state's hierarchy below AggregateState, which hold what it builds
    let private OwnTypes (stateType: Type) =
        Seq.unfold (fun (t: Type) ->
            if isNull t || (t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<AggregateState<_>>) || t = typeof<obj> then None
            else Some (t, t.BaseType)) stateType
        |> Seq.toArray

    /// The state's [<Snapshot>], when it has one
    let Of (stateType: Type) : SnapshotAttribute option =
        stateType.GetCustomAttribute<SnapshotAttribute>() |> Option.ofObj

    /// Whether storing from version before to version after reaches a snapshot
    let IsDue (snapshot: SnapshotAttribute) (before: int64) (after: int64) =
        after / int64 snapshot.Every > before / int64 snapshot.Every

    /// The fingerprint of the state's shape and the snapshot's Version, once the state is checked to round-trip as
    /// JSON. Fails with InvalidOperationException naming what a snapshot would lose
    let FingerprintOf (stateType: Type) (snapshot: SnapshotAttribute) : string =
        Checked.GetOrAdd(stateType, fun stateType ->
            let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly
            let lost =
                OwnTypes stateType
                |> Array.collect (fun t ->
                    Array.append
                        // Fields other than the backing fields of properties (member val X … = name@)
                        (t.GetFields(flags) |> Array.filter (fun field -> not (field.Name.EndsWith "@")) |> Array.map (fun field -> field.Name))
                        // Properties a snapshot can read but not write back
                        (t.GetProperties(flags)
                         |> Array.filter (fun property ->
                             not (isNull (property.GetGetMethod())) && property.GetIndexParameters().Length = 0
                             && isNull (property.GetSetMethod()) && not (isNull (property.GetSetMethod(true))))
                         |> Array.map (fun property -> property.Name)))
            if lost.Length > 0 then
                raise (InvalidOperationException(
                    sprintf "%s can't be snapshotted: a snapshot would lose %s. Keep its state in public properties with public setters (member val X = … with get, set)."
                        stateType.Name (String.Join(", ", lost))))
            let properties =
                stateType.GetProperties(BindingFlags.Instance ||| BindingFlags.Public)
                |> Array.filter (fun property -> not (isNull (property.GetSetMethod())))
                |> Array.map (fun property -> sprintf "%s:%s" property.Name (NameOf property.PropertyType))
                |> Array.sort
            let shape = String.Join("|", Array.append [| NameOf stateType; string snapshot.Version |] properties)
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes shape)).Substring(0, 16))

    let Serialize (stateType: Type) (state: obj) : string =
        JsonSerializer.Serialize(state, stateType, EventEncoding.JsonOptions)

    let Deserialize (stateType: Type) (json: string) : obj =
        JsonSerializer.Deserialize(json, stateType, EventEncoding.JsonOptions)
