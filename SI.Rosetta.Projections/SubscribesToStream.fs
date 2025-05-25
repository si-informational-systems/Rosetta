namespace SI.Rosetta.Projections

open System

[<AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)>]
type SubscribedToStream(name: string) =
    inherit Attribute()
    member val Name = name with get, set

[<AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)>]
type InactiveProjection() =
    inherit Attribute() 