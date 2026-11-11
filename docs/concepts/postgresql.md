---
title: PostgreSQL
description: Read models and state-based Aggregates as rows of your own PostgreSQL tables, and the Outbox in PostgreSQL.
---

# PostgreSQL

PostgreSQL holds read models and [state-based Aggregates](#state-based-aggregates) as rows of your own tables, mapped the same way, and the [Outbox](#the-outbox) in a table of Rosetta's.

With PostgreSQL, a read model is a row of your table, and the Handlers store, load and delete it by key exactly as with documents: they take `ISqlStore` instead of `INoSqlStore`, and nothing else changes. So the [Test Kit](testing.md#projections) runs them in memory too. The tables are yours: your migrations create them, with their indexes, views and anything else, and the query side reads them with whatever SQL it likes. Rosetta only writes the rows.

## The read models

```fsharp
open System
open System.ComponentModel.DataAnnotations          // [<Key>]
open System.ComponentModel.DataAnnotations.Schema   // [<Table>], [<Column>]

[<CLIMutable>]
type Reservation = {                   // reservations
    Id: string                         // id: the key, by convention
    HubId: string                      // hub_id
    SessionDate: DateOnly              // date
    StartsAt: DateTime                 // timestamptz, written as UTC
    Total: decimal                     // numeric
    PromoCode: string option           // a nullable column
    GamesPlayed: string list           // text[]
    Channel: Channel                   // an enum or a union without fields: its name, as text
    Pricing: Pricing }                 // anything else (a record, a union with fields): jsonb

[<CLIMutable; Table("area_hours")>]     // the name, when the convention doesn't fit
type AreaHour = {
    [<Key>] AreaId: string             // a composite key: [<Key>] on each part, in order
    [<Key>] HourStart: DateTime
    ReservationId: string
    Guests: int }
```

## The tables, in your migrations

```sql
create table reservations (
    id text primary key,
    hub_id text not null,
    session_date date not null,
    starts_at timestamptz not null,
    total numeric(12, 2) not null,
    promo_code text,
    games_played text[] not null,
    channel text not null,
    pricing jsonb not null);

create table area_hours (
    area_id text not null,
    hour_start timestamptz not null,
    reservation_id text not null,
    guests int not null,
    primary key (area_id, hour_start));
```

## The Projection

```fsharp
[<HandlesStream("$ce-Reservations")>]
type ReservationAnalyticsProjection() =
    inherit Projection<ReservationEvents>()
    interface IAmHandledBy<ReservationAnalyticsHandler, ReservationEvents>
    interface IProjectInto<Reservation>       // the tables it writes: checked at startup, emptied on a rebuild
    interface IProjectInto<AreaHour>

and ReservationAnalyticsHandler(store: ISqlStore) =
    interface IProjectionHandler<ReservationEvents> with
        member this.Handle(event, checkpoint) =
            task {
                match event with
                | ReservationEvents.Made e ->
                    do! store.StoreAsync(
                            { Id = e.Id; HubId = e.HubId; SessionDate = e.SessionDate; StartsAt = e.StartsAt; Total = e.Total
                              PromoCode = e.PromoCode; GamesPlayed = e.Games; Channel = e.Channel; Pricing = e.Pricing })
                    do! store.StoreManyAsync(
                            [| for hour in e.Hours -> { AreaId = e.AreaId; HourStart = hour; ReservationId = e.Id; Guests = e.Guests } |])
                | ReservationEvents.Repriced e ->
                    let! reservation = store.LoadAsync<Reservation>(e.Id)
                    if not (isNull (box reservation)) then
                        do! store.StoreAsync({ reservation with Total = e.Total })
                | ReservationEvents.Cancelled e ->
                    do! store.DeleteAsync<Reservation>(e.Id)
                    do! store.DeleteManyAsync<AreaHour>(
                            [| for hour in e.Hours -> box (e.AreaId, hour) |])   // a composite key, as a tuple
            }
```

Register it with `PostgreSQL` as the store, or next to a document store:

```fsharp
open SI.Rosetta.Projections.HostBuilder

builder.UseProjectionsWith<KurrentDB, PostgreSQL>(typeof<ReservationAnalyticsProjection>.Assembly) |> ignore
// or documents and tables in one app: RavenDB or MongoDB first, then PostgreSQL
builder.UseProjectionsWith<KurrentDB, RavenDB, PostgreSQL>(typeof<ReservationAnalyticsProjection>.Assembly) |> ignore
```

```csharp
using Stores = SI.Rosetta.Projections.HostBuilder;

builder.UseProjectionsWith<Stores.KurrentDB, Stores.RavenDB, Stores.PostgreSQL>(typeof(ReservationAnalyticsProjection).Assembly);
```

## How the rows are mapped

| Field type | Column | |
|---|---|---|
| `string`, `int16`, `int`, `int64`, `single`, `double`, `decimal`, `bool`, `Guid`, `byte[]` | `text`, `smallint`, `int`, `bigint`, `real`, `double precision`, `numeric`, `boolean`, `uuid`, `bytea` | as Npgsql maps them |
| `DateTime`, `DateTimeOffset` | `timestamptz` | written as UTC (a `DateTime` of unspecified kind is taken as UTC) |
| `DateOnly`, `TimeOnly`, `TimeSpan` | `date`, `time`, `interval` | |
| an enum, a union whose cases have no fields | `text` | the name of the value or case |
| an array or F# list of the plain types in the first three rows | an array (`text[]`, `int[]`…) | arrays of enums or unions go to `jsonb` |
| `'T option` | the column of `'T`, nullable | `None` is `NULL` |
| anything else: records, unions with fields, maps | `jsonb` | as Rosetta writes events (System.Text.Json) |

- **Names:** a table is the snake_case plural of the type (`Reservation` → `reservations`, `AreaHour` → `area_hours`), a column the snake_case of the field (`SessionDate` → `session_date`), in the configured `Schema`. `[<Table("…", Schema = "…")>]` and `[<Column("…")>]` name them otherwise.
- **Keys:** `Id`, or `[<Key>]` on each part of a composite key. `LoadAsync`, `LoadManyAsync`, `DeleteAsync` and `DeleteManyAsync` take a composite key as a tuple of its parts, in order.
- **Read models** are F# records, or classes with a parameterless constructor and settable properties.
- **Writes:** `StoreAsync` upserts the whole row on its key (`insert … on conflict (key) do update`). An update is load, change, store; `LoadAsync` returns `null` for a missing row, so check it. Within an event, the writes go to PostgreSQL as one batch, sent before the next load and with the checkpoint, so a Handler reads back what it wrote.
- **Checked at startup:** each `IProjectInto` table must exist, have a column for every field, and have a primary key or unique index on exactly the key columns. When it doesn't, the Projection retries (your migrations may still be running) and reports what's missing, until it does. Column types aren't checked: a mismatch fails on the first write or read.
- **Rebuilds** [truncate](projections.md#rebuilding-a-projection) every `IProjectInto` table of the Projection, in one statement.

## Configuration

```json
{
  "PostgreSQL": {
    "ConnectionString": "Host=localhost;Port=5432;Database=app;Username=app;Password=…",
    "Schema": "public"
  }
}
```

- `ConnectionString` is required. Rosetta keeps its own connection pool for it, apart from any `NpgsqlDataSource` the
  app registers.
- `Schema` is optional: the schema of the tables that don't name theirs with `[<Table(…, Schema = …)>]`, `public`
  unless set. Set it when all your read model (and Aggregate state) tables live in one schema of their own. Rosetta qualifies every table
  with its schema, so the connection string's `Search Path` doesn't change where it writes.
- Rosetta keeps its checkpoints in its own `rosetta.checkpoints` table (`id`, `value`, `version`, `updated_at`), and
  the Outbox in `rosetta.outbox` (and its publisher's lease in `rosetta.outbox_leases`), which it creates, so the user needs the `CREATE` privilege on the database. A missing
  database is created when the Projections start or the Outbox first records events, which needs `CREATEDB`; without
  it, create the database first. While PostgreSQL is still booting,
  that preparation retries for about a minute.

## One store per Projection

With both stores registered, a Projection whose Handlers take `INoSqlStore` writes to RavenDB or MongoDB, one whose Handlers take `ISqlStore` to PostgreSQL, each in its own store's transactions and with its checkpoint there. One whose Handlers take both fails at startup: a transaction can't span two databases, so split it in two.

```fsharp
// documents and tables in one app: the document store first
builder.UseProjectionsWith<KurrentDB, RavenDB, PostgreSQL>(typeof<PersonProjection>.Assembly) |> ignore
```

The kind is decided by the Handlers' constructors: a Handler that takes `ISqlStore` makes its Projection a SQL one.

## Guidelines for PostgreSQL read models

- **Documents or tables?** Documents for views read whole, the way a screen shows them; tables for facts you'll aggregate, filter and join with SQL, in queries nobody has written yet (analytics, reports, exports).
- **The migrations own the schema.** Create and change the tables in the app's migrations, deployed before the Projections that need them. When a read model gains a field, add its column in a migration, and bump the Projection's [`ProjectionVersion`](projections.md#rebuilding-a-projection) when the existing rows need the new value.
- **Match the table to the record.** Make a column `not null` unless its field is an `option`; use `timestamptz` for `DateTime` and `DateTimeOffset`, `numeric` for `decimal`, `jsonb` for nested records. A column the record doesn't have is never written, so give it a default or let it be null.
- **Key on what the events carry.** A Handler finds a row again by its key, so the key must come from the events: the Aggregate's `Id`, or a composite key (`area_id`, `hour_start`) for one row per thing per period.
- **Only the Projection writes its tables.** Read them from anywhere (views, reports, the API), but don't write them elsewhere, and don't let other tables reference them with foreign keys: a rebuild truncates them. Foreign keys between the tables of one Projection are fine, as they're truncated together.
- **Index for your queries.** Rosetta only needs the key; add the indexes your queries need in the same migrations.
- **Counters count once.** An analytics row that adds one per event (load, add one, store) is safe: the event's writes and its checkpoint commit together, and a retried event starts from the rows as they were.
- **Size Rosetta's pool.** Each event holds a connection from Rosetta's own pool while its Handlers run, so `Maximum Pool Size` in the connection string (100 by default) must cover the Projections running at once, besides the app's own pool.
- **Test the Handlers in memory, the mapping once.** The [Projections Test Kit](testing.md#projections) runs `ISqlStore` Handlers without a database. Add one integration test that applies your migrations to a real PostgreSQL and starts the Projections, so the startup check validates every table.

## State-based Aggregates

A state-based Aggregate's state is a row of your table too, mapped [as read models are](#how-the-rows-are-mapped): the snake_case plural of the state type (`ParcelAggregateState` → `parcel_aggregate_states`) in the configured `Schema`, or `[<Table(…, Schema = …)>]`; a column per public property with a setter, snake_case or `[<Column(…)>]`, with the same column types. `Id` is the key and `Version` a regular `bigint` column.

```fsharp
type ParcelAggregateState() =                                       // parcel_aggregate_states
    inherit AggregateState<ParcelEvents>()                          // Id → id (the key), Version → version
    member val Recipient = "" with get, set                         // text
    member val Stops : string list = [] with get, set               // text[]
    member val DeliveredAt : DateTime option = None with get, set   // timestamptz, nullable
```

```sql
create table parcel_aggregate_states (
    id text primary key,
    version bigint not null,
    recipient text not null,
    stops text[] not null,
    delivered_at timestamptz);
```

Register `PostgreSQL` as the state-based store; the Handlers take `IStateBasedAggregateRepository` as with the other stores:

```fsharp
open SI.Rosetta.Aggregates.HostBuilder

builder.UseAggregatesWithoutEventSourcing<PostgreSQL>(typeof<ParcelAggregateHandler>.Assembly) |> ignore
// or next to Event-Sourced Aggregates, with the Outbox in PostgreSQL too
builder.UseAggregatesWith<KurrentDB, PostgreSQL>(typeof<ParcelAggregateHandler>.Assembly) |> ignore
builder.UseOutboxWith<PostgreSQL>() |> ignore
```

```csharp
using Stores = SI.Rosetta.Aggregates.HostBuilder;

builder.UseAggregatesWith<Stores.KurrentDB, Stores.PostgreSQL>(typeof(ParcelAggregateHandler).Assembly);
builder.UseOutboxWith<Stores.PostgreSQL>();
```

- **Optimistic concurrency.** A new Aggregate is inserted unless its `Id` exists (`insert … on conflict (id) do nothing`), a loaded one updated only while its row is still at the version it was loaded at (`update … where id = … and version = …`). When no row changes, the attempt fails with `ConcurrencyException` and nothing is written, its Outbox events included: the handler runs the command again at once, as with KurrentDB (see [Conflicts](aggregates.md#conflicts)), and a conflict that outlasts that reaches the caller (see [Handle commands from a message bus](../guides/recipes.md#handle-commands-from-a-message-bus)).
- **Checked on first use.** The first time a state type is used in a process, its table is checked: it exists, has a column for every property, and a primary key or unique index on exactly `id`. When it doesn't, every command on that type fails with an `InvalidOperationException` saying what's missing (see [Troubleshooting](../guides/troubleshooting.md#when-a-command-runs)), until the table matches: the check isn't remembered when it fails, so the next command after the migrations works. Column types aren't checked.
- **The tables are yours.** Rosetta never creates or changes them: create them in the app's migrations, and add a property's column before deploying the state that has it. The state is plain rows, so query it with SQL from anywhere; change it only through commands, as a write that leaves `version` alone goes unnoticed by the concurrency check.
- Aggregates, the Outbox and Projections in one host use the same `PostgreSQL` section and share Rosetta's one connection pool.

## The Outbox

`UseOutboxWith<PostgreSQL>` records the [PublishedEvents](outbox.md) in a table of Rosetta's, `rosetta.outbox` (`rosetta.` and the snake_case of `Outbox:CollectionName`: `ParcelEvents` → `rosetta.parcel_events`), which it creates on first use, with the database when it's missing. Its columns are in the [Stored format](../reference/stored-format.md#an-event-in-the-outbox).

- With state-based Aggregates in PostgreSQL, the events are recorded **in the state's transaction**: both are written, or neither. With Event-Sourced Aggregates or another state store, they're recorded right after the Aggregate is stored.
- PostgreSQL has no TTL: with `Outbox:EventTTLMinutes`, each row has its `expires_at`, and the Outbox deletes the expired rows itself (see [Expiry](outbox.md#expiry)).
- `UseOutboxPublisher` publishes the rows `where published_at is null` and sets `published_at` (see [Publishing the events](outbox.md#publishing-the-events) and [Publish the Outbox](../guides/recipes.md#publish-the-outbox)). Its lease is a row of `rosetta.outbox_leases`, by the database's clock. On a table created before, the column is added and the rows already there count as unpublished.
