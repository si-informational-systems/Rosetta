namespace SI.Rosetta.Common

open System

/// The name an event record is stored under, instead of its type name, so the record type can be renamed
/// without touching the events already stored: [<EventType("PersonRegistered")>] type PersonSignedUp = { ... }.
/// The name is what Rosetta writes, reads back and generates Custom Projections with. Keep it once events are stored;
/// a changed shape (renamed, removed or retyped fields) still needs an IEventUpcaster
[<AllowNullLiteral>]
[<AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)>]
type EventType(name: string) =
    inherit Attribute()
    do if String.IsNullOrWhiteSpace name then invalidArg (nameof name) "An event type name can't be empty."
    member this.Name = name
