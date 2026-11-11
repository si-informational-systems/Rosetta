---
title: Stored format
description: How Rosetta writes events to KurrentDB and the Outbox — data, metadata and JSON shapes.
---

# Stored format

## An event in KurrentDB

| | |
|---|---|
| Stream | the Aggregate's `Id` (`People-1`) |
| Event type | the record's stored name: its `[<EventType>]` name, or its type name (`PersonRegistered`) |
| Data | the case's **record** as compact JSON (not the union case) |
| Metadata | the headers below, as JSON |

```json title="data"
{"Id":"People-1","Name":"Ada"}
```

```json title="metadata"
{
  "EventClrTypeName": "People.PL.PersonRegistered, People.PL",
  "EventsDiscriminatedUnionClrTypeName": "People.PL.PersonEvents, People.PL",
  "DiscriminatedUnionCaseType": "PersonRegistered",
  "AggregateClrTypeName": "People.Domain.PersonAggregate, People.Domain",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

- **`DiscriminatedUnionCaseType`** is the stored name, and the only header Rosetta needs to read an event: it finds the
  DU case whose record has that name. The CLR type names are informational.
- `traceparent`/`tracestate` are written when the command ran inside a trace.

## JSON shapes

Events are written with System.Text.Json and FSharp.SystemTextJson (`EventEncoding.JsonOptions`), in the shapes
Newtonsoft.Json wrote before 1.26, so older and newer events read the same:

| F# value | JSON |
|---|---|
| record | an object with the fields' names |
| `Some 5` / `None` | `5` / `null` |
| `ValueSome 5` / `ValueNone` | `{"Case":"ValueSome","Fields":[5]}` / `null` |
| union inside a record | `{"Case":"Crate","Fields":[2,3]}`; a case without fields: `{"Case":"Empty"}` |
| list, array, set | an array |
| map | an object |
| enum | its name |
| tuple | `{"Item1":…,"Item2":…}` |
| `decimal`, `DateTime`, `DateOnly`, `Guid` | as System.Text.Json writes them |

Reading is lenient: property names are matched case-insensitively, numbers may be strings, and both option shapes
(`5` and `{"Case":"Some","Fields":[5]}`) are read.

So a C# service can read the events into C# records of the same shape; only DUs nested in a record are F#-specific.

## An event in the Outbox

The same stored name and data, in a document per event (see [Outbox](../concepts/outbox.md#the-recorded-events)):

```json
{
  "Id": "8c5d0f5e-…",
  "Aggregate": "Person",
  "AggregateId": "People-1",
  "Version": 1,
  "Position": 0,
  "EventType": "PersonRegistered",
  "Data": "{\"Id\":\"People-1\",\"Name\":\"Ada\"}",
  "RecordedAt": "2026-11-11T10:15:00Z",
  "TraceParent": "00-…",
  "TraceState": null
}
```

In PostgreSQL, a row of `rosetta.outbox` (`rosetta.` and the snake_case of `Outbox:CollectionName`), which the Outbox
creates:

```sql
create table rosetta.outbox (
    id uuid primary key,
    aggregate text not null,
    aggregate_id text not null,
    version bigint not null,
    position int not null,
    event_type text not null,
    data json not null,              -- json, not jsonb: the record's JSON text exactly as KurrentDB stores it
    recorded_at timestamptz not null,
    expires_at timestamptz,          -- with Outbox:EventTTLMinutes; a partial index covers it
    trace_parent text,
    trace_state text);
```

`EventEncoding.Decode<PersonEvents> upcasters recorded.EventType recorded.Data` reads it back.

## State-based Aggregates

| Store | Document or row |
|---|---|
| RavenDB | the state object (its properties), id = `Id`; `Version` in `@metadata["Rosetta-Aggregate-Version"]` |
| MongoDB | the state object in the pluralized state type name collection, `_id` = `Id`, with a `version` field; camelCase names, F# lists/maps/unions as described in [Configuration](../getting-started/configuration.md#mongodb) |
| PostgreSQL | a row of the app's table, the snake_case plural of the state type name (`PersonAggregateState` → `person_aggregate_states`): `id` (the key), `version bigint`, and a column per property, mapped as [PostgreSQL read models](../concepts/postgresql.md#how-the-rows-are-mapped) |

## Checkpoints

| Store | Where |
|---|---|
| RavenDB | document `Checkpoints-{Projection}` (`Value`, `Version`) |
| MongoDB | collection `Checkpoints`, `_id` = `Checkpoints-{Projection}` |
| PostgreSQL | `rosetta.checkpoints (id text primary key, value bigint, version int, updated_at timestamptz)` |

`Value` is one-based: the number of the stream's events the Projection has handled.
