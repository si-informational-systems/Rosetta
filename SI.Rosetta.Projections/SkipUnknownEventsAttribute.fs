namespace SI.Rosetta.Projections

open System

/// The projection skips (and logs) events whose record type its events DU has no case for, instead of stopping on them.
/// For events added to the domain before this projection handles them
[<AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)>]
type SkipUnknownEvents() =
    inherit Attribute()
