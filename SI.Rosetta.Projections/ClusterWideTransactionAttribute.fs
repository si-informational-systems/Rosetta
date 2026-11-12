namespace SI.Rosetta.Projections

open System
open Microsoft.FSharp.Reflection
open SI.Rosetta.Common

/// RavenDB only: each event's transaction is a cluster-wide one (TransactionMode.ClusterWide), so a handler can change
/// compare-exchange values in it (RavenDbProjectionTransaction.Current) together with the event's documents and its
/// checkpoint, and the event is handled again when one of them changed in between (ConflictRetries). MongoDB and
/// PostgreSQL refuse it when the projection starts.
///
/// Without arguments, every event's transaction is cluster-wide. With event record types
/// (`[<ClusterWideTransaction(typeof<ReservationMade>, typeof<ReservationCancelled>)>]`), only those events' are; the
/// others are handled in a normal session. Both kinds can write the same documents: a cluster-wide transaction loads
/// each document before writing it, so it passes the atomic guards the other kind leaves behind
[<AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)>]
type ClusterWideTransaction([<ParamArray>] events: Type[]) =
    inherit Attribute()

    /// The event record types whose transactions are cluster-wide; empty for every event
    member this.Events: Type[] = if isNull events then [||] else events

    static member private Of(projectionType: Type) =
        if isNull projectionType then None
        else
            match Attribute.GetCustomAttribute(projectionType, typeof<ClusterWideTransaction>, false) with
            | :? ClusterWideTransaction as attribute -> Some attribute
            | _ -> None

    /// Whether the projection type has [<ClusterWideTransaction>], for any of its events
    static member IsOn(projectionType: Type) =
        (ClusterWideTransaction.Of projectionType).IsSome

    /// Whether the event's transaction is cluster-wide: the projection type has [<ClusterWideTransaction>] without
    /// events, or names the event's record type
    static member IsOn(projectionType: Type, eventType: Type) =
        match ClusterWideTransaction.Of projectionType with
        | None -> false
        | Some attribute -> attribute.Events.Length = 0 || Array.contains eventType attribute.Events

    /// Fails the start of a projection with [<ClusterWideTransaction>] on a store that has no such transactions
    static member Refuse(store: string, projectionType: Type) =
        if ClusterWideTransaction.IsOn projectionType then
            raise (InvalidOperationException(
                sprintf "Projection %s has [<ClusterWideTransaction>], which only RavenDB has, but it projects into %s. Remove the attribute, or project it into RavenDB."
                    projectionType.Name store))

    /// Fails when the projection's [<ClusterWideTransaction>] names a type that isn't the record of one of its events
    /// (the events DU eventsType): its transactions would never be cluster-wide
    static member Check(projectionType: Type, eventsType: Type) =
        match ClusterWideTransaction.Of projectionType with
        | Some attribute when attribute.Events.Length > 0 ->
            let records =
                if FSharpType.IsUnion eventsType then FSharpType.GetUnionCases eventsType |> Array.map GetUnionCaseType
                else [||]
            match attribute.Events |> Array.filter (fun t -> isNull t || not (Array.contains t records)) with
            | [||] -> ()
            | unknown ->
                raise (InvalidOperationException(
                    sprintf "Projection %s has [<ClusterWideTransaction>] for %s, which %s no event of %s: name the record types of its events, or no type for every event."
                        projectionType.Name
                        (unknown |> Array.map (fun t -> if isNull t then "null" else t.Name) |> String.concat ", ")
                        (if unknown.Length = 1 then "is" else "are")
                        eventsType.Name))
        | _ -> ()
