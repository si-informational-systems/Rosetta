---
title: Event evolution
description: Stable event names, upcasters, and what happens to events a DU does not know.
---

# Event evolution

An event is stored under the **name of its record type**, and read back into the DU case whose record has that name. Changing a record would therefore leave the events already in KurrentDB unreadable, unless you use one of these:

| The record… | Old events stay readable with |
|---|---|
| gains a field | nothing: older events read it as its default (`null`, `0`, `None`) |
| loses a field | nothing: the stored value is ignored |
| is renamed | `[<EventType("OldName")>]` on the renamed record |
| renames, retypes or splits fields, or is renamed without an `[<EventType>]` | an upcaster |

## The stored format

An event's data is its record as plain JSON (System.Text.Json), and its metadata names the record it's stored under, so the events don't depend on F#: a C# service can read them into C# records. F# options are written as their value, or `null` for `None`. DUs inside a record keep the `{"Case": …, "Fields": […]}` shape, so they're F#-only.

## Stable event type names

`[<EventType>]` fixes the name a record is stored under (and that Custom Projections link it by), so the record type can be renamed freely:

```fsharp
open SI.Rosetta.Common

[<EventType("PersonRegistered")>]   // the name its events were stored under
type PersonSignedUp = { Id: string; Name: string }
```

Keep the name once events are stored. To make every rename safe from the start, give each event record an `[<EventType>]` with its current name. Two cases of one DU can't share a name: reading that DU fails with an error naming them.

## Upcasters

An **upcaster** rewrites old events, as they're read, into the shape the current DU expects:

```fsharp
open SI.Rosetta.Common   // where IEventUpcaster lives, so a Published Language project can hold its upcasters

/// Older events were PersonCreated, with FullName for Name
type PersonCreatedUpcaster() =
    interface IEventUpcaster with
        member _.Upcast(ev) =
            match ev.TypeName with
            | "PersonCreated" -> { ev.RenameField("FullName", "Name") with TypeName = "PersonRegistered" }
            | _ -> ev

services.AddSingleton<IEventUpcaster, PersonCreatedUpcaster>()
```

`ev.Data` is the record as a System.Text.Json `JsonObject`, parsed for each event read, so an upcaster may change it in place. `RenameField` covers the common case. For other changes, use the `JsonObject` API directly: a JSON node belongs to one object, so remove it (or `DeepClone` it) before adding it under another name.

- Register them in **every service that reads the events**: the Aggregates service rebuilds Aggregates from their stored events, too. Register them as singletons: one instance serves every Projection and command at once, so keep them stateless.
- `TypeName` is the name the event is stored under (its record's `[<EventType>]` name, or the record type's name), and the upcaster returns the name of the record it should be read as.
- Every event read from KurrentDB, by Aggregates and Projections alike, goes through every registered upcaster, **in registration order**, each getting the previous one's output. So register a chain oldest step first (v1→v2 before v2→v3), or upcast every old shape of an event straight to the current one in a single upcaster, which doesn't depend on order. Only the reading side changes: the stored events stay as they were written.
## Unknown events

- An event whose record type the DU has no case for (even after upcasting) throws an `UnknownEventException` that names the event, its stream and its number.
- An **Aggregate** always fails on such an event: rebuilding its state without it would be wrong.
- A **Projection** stops on it, unless it's marked `[<SkipUnknownEvents>]`. Then it logs the event (a warning the first time per event type) and moves past it. Use that for Projections that may see events added to the domain before they handle them:

```fsharp
[<SkipUnknownEvents>]
[<HandlesStream(ProjectionStreamNames.PersonStream)>]
type PersonProjection() =
    inherit Projection<PersonEvents>()
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents>
```

## Rolling out a new event

1. Add the case and its record (with an `[<EventType>]`) to the events DU, and handle it in the state's `ApplyEvent`.
2. Handle it in every Projection of that stream, or mark the ones that don't care `[<SkipUnknownEvents>]`.
3. Deploy the **readers first** (the Projections services), then the **writers** (the Aggregates service). In the other
   order, a Projection meets an event its deployed DU doesn't know, and stops.

## Testing upcasters

Upcasters are tested on their own with the Upcasting TestKit: see [Testing](testing.md#upcasters).
