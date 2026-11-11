---
title: Commands and events
description: The Published Language — commands and events as F# discriminated unions of records.
---

# Commands and events

An Aggregate's **commands** and **events** are its Published Language: what other code may send to it, and what it
tells the world happened. In Rosetta each is an F# discriminated union (DU) that implements a marker interface from
`SI.Rosetta.Common`.

```fsharp
open SI.Rosetta.Common

type RegisterPerson = { Id: string; Name: string }
type ChangePersonName = { Id: string; Name: string }

type PersonRegistered = { Id: string; Name: string }
type PersonNameChanged = { Id: string; OldName: string; NewName: string }

[<RequireQualifiedAccess>]
type PersonCommands =
    | Register of RegisterPerson
    | ChangeName of ChangePersonName
    interface IAggregateCommands

[<RequireQualifiedAccess>]
type PersonEvents =
    | Registered of PersonRegistered
    | NameChanged of PersonNameChanged
    interface IAggregateEvents
```

## The rules

| Rule | Why |
|---|---|
| Every case wraps **exactly one record**. | The record is what's serialized and stored; the case is only how F# code matches it. |
| A record's **stored name** must be unique within its DU. | An event is stored and read by its record's name, never by the case name. Two cases under one name fail on the first read of that DU (`InvalidOperationException`). |
| Commands implement `IAggregateCommands`, events `IAggregateEvents`. | Registration, the repositories and the TestKits find them by these interfaces. |
| Command records carry the Aggregate's `Id: string`. | The handler passes it to `IdempotentlyCreateAggregate`/`IdempotentlyUpdateAggregate`, and the Aggregates TestKit requires it. |
| Use `[<RequireQualifiedAccess>]`. | Case names like `Registered` repeat across Aggregates. |
| Commands are imperative, events past tense: `RegisterPerson` → `PersonRegistered`. | The domain's language; event record names become the stored event types, so they should read as facts. |

## The stored name of an event

An event is stored in KurrentDB (and the Outbox) under its **record's name**:

- the name in `[<EventType("…")>]` when the record has one;
- otherwise the record type's name (`PersonRegistered`).

```fsharp
[<EventType("PersonRegistered")>]      // stored under this name, whatever the record is called
type PersonSignedUp = { Id: string; Name: string }
```

Give every event record an `[<EventType>]` with its current name from the start: the record can then be renamed
freely, and [Custom Projections](custom-projections.md) keep linking it. Once events are stored, never change the name.
See [Event evolution](event-evolution.md).

## Where the messages live

Put the commands, events and their [upcasters](event-evolution.md#upcasters) in a **Published Language** project that
references only `SI.Rosetta.Common`. Other services (and Projections) reference that project, or the same records
through a package, without depending on the Aggregates or any database client. See
[Project structure](../getting-started/project-structure.md).

## Integration events

Events an Aggregate adds to its `PublishedEvents` are its **integration events**, for the outside world. They're
usually cases of the same events DU. With the [Outbox](outbox.md) they're recorded with the Aggregate's state for
publishing.

## Custom Projection DUs

A third kind of DU, `ICustomProjectionEvents`, lists events from **several** Aggregates for a
[Custom Projection](custom-projections.md). Its cases reuse the Aggregates' event records.
