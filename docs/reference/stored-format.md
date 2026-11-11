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
| Metadata | none from Rosetta; KurrentDB.Client adds the trace when the command ran inside one |

```json title="data"
{"Id":"People-1","Name":"Ada"}
```

```json title="metadata (inside a trace, written by KurrentDB.Client)"
{"$traceId":"4bf92f3577b34da6a3ce929d0e0e4736","$spanId":"00f067aa0ba902b7"}
```

- **The event type** is all Rosetta needs to read an event: it finds the DU case whose record is stored under that name.
- **Rosetta writes no metadata.** KurrentDB.Client adds `$traceId` and `$spanId` to an append made inside a trace (the
  command's span, or the client's own append span when the app traces the client too), and the projections handling
  the event continue that trace, taking it as sampled (the client doesn't store the flag, nor `tracestate`). Outside a
  trace the metadata is empty.
- **Before 1.38** the metadata held `DiscriminatedUnionCaseType` (the stored name again), CLR type names and
  `traceparent`: often more bytes than the event's data, and nothing but `traceparent` was read.

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
  "EventsUnionClrType": "People.PL.PersonEvents, People.PL",
  "EventClrType": "People.PL.PersonRegistered, People.PL",
  "EventType": "PersonRegistered",
  "Data": "{\"Id\":\"People-1\",\"Name\":\"Ada\"}",
  "RecordedAt": "2026-11-11T10:15:00Z",
  "TraceParent": "00-…",
  "TraceState": null,
  "PublishedAt": null
}
```

`PublishedAt` is set when the Outbox's [publisher](../concepts/outbox.md#publishing-the-events) has published the
event; until then it's `null`. A document recorded before the publisher was registered has no `PublishedAt` at all,
and isn't published (RavenDB, MongoDB). With `EventTTLMinutes` and a publisher, the expiry (`@expires` in RavenDB,
`expiresAt` in MongoDB) is set when the event is published. MongoDB writes the fields in camelCase (`publishedAt`,
`eventsUnionClrType`, `eventClrType`).

`EventsUnionClrType` and `EventClrType` are recorded since 1.38; a record from before has neither (`null`), and the
built-in publisher can't decode it (see [Upgrading from 1.37](../concepts/outbox.md#upgrading-from-137)).

In PostgreSQL, a row of `rosetta.outbox` (`rosetta.` and the snake_case of `Outbox:CollectionName`), which the Outbox
creates:

```sql
create table rosetta.outbox (
    id uuid primary key,
    aggregate text not null,
    aggregate_id text not null,
    version bigint not null,
    position int not null,
    events_union_clr_type text,      -- since 1.38; null in rows recorded before
    event_clr_type text,             -- since 1.38; null in rows recorded before
    event_type text not null,
    data json not null,              -- json, not jsonb: the record's JSON text exactly as KurrentDB stores it
    recorded_at timestamptz not null,
    expires_at timestamptz,          -- with Outbox:EventTTLMinutes (null until published, with a publisher); a partial index covers it
    trace_parent text,
    trace_state text,
    published_at timestamptz);       -- set by the publisher; a partial index on (recorded_at, position) where it's null
```

`published_at`, `events_union_clr_type` and `event_clr_type` are added to a table created without them (`alter table
… add column if not exists`). The publisher's lease is a row of a table the Outbox also creates:

```sql
create table rosetta.outbox_leases (name text primary key, owner text not null, expires_at timestamptz not null);
```

`OutboxEvents.Decode upcasters recorded` reads it back into its events union, as the publisher does.

## The Outbox publisher's lease

| Store | Where | Fields |
|---|---|---|
| RavenDB | document `OutboxLeases/{CollectionName}` | `Owner`, `ExpiresAt` |
| MongoDB | collection `OutboxLeases`, `_id` = `CollectionName` | `owner`, `expiresAt` |
| PostgreSQL | `rosetta.outbox_leases`, `name` = the Outbox table's name (`outbox`) | `owner`, `expires_at` |

The owner is the instance publishing, `{machine name}/{a GUID per process}`.

## A snapshot

An event-sourced Aggregate whose state has [`[<Snapshot(n)>]`](../concepts/aggregates.md#snapshots):

| | |
|---|---|
| Stream | `RosettaSnapshots-{Id}` (`RosettaSnapshots-People-1`), with `$maxCount` 1 |
| Event type | `RosettaSnapshot` |
| Data | the state as JSON (`EventEncoding.JsonOptions`): its public settable properties, `Id` and `Version` included |
| Metadata | `Version` (the Aggregate's version it's the state at), `Fingerprint` (of the state's shape and the `[<Snapshot>]` `Version`), `LastEventId` (the id of the last event it covers) |

```json title="metadata"
{"Version":200,"Fingerprint":"3F1A9C0B7D2E4F61","LastEventId":"5b4e5a1c-6f0e-4c39-9f3e-7f1c1d1e2a01"}
```

## Custom Projections an application defined

The stream `RosettaCustomProjections-{owner}` (`$maxCount` 10), one `CustomProjectionsDefined` event each time the set
changes; its data lists each projection's name and `cp-` stream:

```json title="data"
[{"Name":"TotalPeopleAndOrganizationsCustomProjection","Stream":"cp-TotalPeopleAndOrganizations"}]
```

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
