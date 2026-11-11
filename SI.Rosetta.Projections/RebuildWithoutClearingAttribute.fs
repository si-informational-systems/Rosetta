namespace SI.Rosetta.Projections

open System

/// The projection is rebuilt without emptying anything first: every handler only writes whole documents or rows
/// (no +1, no appending, no load-modify-store), so replaying its stream over its read models gives what a fresh start
/// would. Without it, a projection that can be rebuilt ([<ProjectionVersion>] above 0, or a custom projection's cp-
/// stream, which is linked again when its definition changes) must declare its read models with IProjectInto, so a
/// rebuild empties them: registering it fails otherwise
[<AttributeUsage(AttributeTargets.Class, AllowMultiple = false)>]
type RebuildWithoutClearing() =
    inherit Attribute()
