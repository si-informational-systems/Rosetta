---
title: PostgreSQL read models
description: Read models as rows of your own PostgreSQL tables, with the same typed store-load-delete by key.
---

# PostgreSQL read models

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
  unless set. Set it when all your read model tables live in one schema of their own. Rosetta qualifies every table
  with its schema, so the connection string's `Search Path` doesn't change where it writes.
- Rosetta keeps its checkpoints in its own `rosetta.checkpoints` table (`id`, `value`, `version`, `updated_at`), which
  it creates, so the user needs the `CREATE` privilege on the database. A missing database is created when the
  Projections start, which needs `CREATEDB`; without it, create the database first. While PostgreSQL is still booting,
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
