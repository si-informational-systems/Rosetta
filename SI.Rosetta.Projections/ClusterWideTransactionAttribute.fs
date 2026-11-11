namespace SI.Rosetta.Projections

open System

/// RavenDB only: each event's transaction is a cluster-wide one (TransactionMode.ClusterWide), so a handler can change
/// compare-exchange values in it (RavenDbProjectionTransaction.Current) together with the event's documents and its
/// checkpoint, and the event is handled again when one of them changed in between (ConflictRetries). MongoDB and
/// PostgreSQL refuse it when the projection starts
[<AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)>]
type ClusterWideTransaction() =
    inherit Attribute()

    /// Whether the projection type has [<ClusterWideTransaction>]
    static member IsOn(projectionType: Type) =
        not (isNull projectionType) && projectionType.IsDefined(typeof<ClusterWideTransaction>, false)

    /// Fails the start of a projection with [<ClusterWideTransaction>] on a store that has no such transactions
    static member Refuse(store: string, projectionType: Type) =
        if ClusterWideTransaction.IsOn projectionType then
            raise (InvalidOperationException(
                sprintf "Projection %s has [<ClusterWideTransaction>], which only RavenDB has, but it projects into %s. Remove the attribute, or project it into RavenDB."
                    projectionType.Name store))
