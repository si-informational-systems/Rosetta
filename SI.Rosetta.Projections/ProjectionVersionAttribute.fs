namespace SI.Rosetta.Projections

open System

/// Rebuilds the projection when the number goes up: when it starts, Rosetta empties the tables (PostgreSQL) or
/// collections (RavenDB, MongoDB) of the read models it declares with IProjectInto, resets its checkpoint, and it
/// projects its stream again from the start. Bump it when a projection's handlers change what they write. Without the
/// attribute, the version is 0
[<AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type ProjectionVersion(version: int) =
    inherit Attribute()
    member this.Version = version
