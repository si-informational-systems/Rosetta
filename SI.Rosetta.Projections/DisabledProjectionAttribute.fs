namespace SI.Rosetta.Projections

open System

[<AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)>]
type DisabledProjection() =
    inherit Attribute() 