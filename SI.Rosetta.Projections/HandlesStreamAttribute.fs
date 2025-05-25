namespace SI.Rosetta.Projections

open System

[<AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)>]
type HandlesStream(name: string) =
    inherit Attribute()
    member val Name = name with get, set