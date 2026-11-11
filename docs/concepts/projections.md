---
title: Projections
description: Read models kept up to date from KurrentDB streams, one transaction per event, in RavenDB, MongoDB or PostgreSQL.
---

# Projections

A **Projection** subscribes to a KurrentDB stream and runs its **Projection Handlers** on every event, which keep
**read models** up to date: documents in RavenDB or MongoDB, or rows of your own [PostgreSQL](postgresql.md) tables.

```fsharp
open SI.Rosetta.Projections

[<AutoOpen>]
module ProjectionStreamNames =
    [<Literal>]
    let PersonStream = "$ce-People"

[<CLIMutable>]
type Person = { Id: string; Name: string }

[<HandlesStream(ProjectionStreamNames.PersonStream)>]
type PersonProjection() =
    inherit Projection<PersonEvents>()
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents>
    interface IProjectInto<Person>

and PersonProjectionHandler(store: INoSqlStore) =
    interface IProjectionHandler<PersonEvents> with
        member this.Handle(event, checkpoint) =
            task {
                match event with
                | PersonEvents.Registered e ->
                    do! store.StoreAsync({ Id = e.Id; Name = e.Name } : Person)
                | PersonEvents.NameChanged e ->
                    let! person = store.LoadAsync<Person>(e.Id)
                    if not (isNull (box person)) then
                        do! store.StoreAsync({ person with Name = e.NewName })
            }
```

## The pieces

| | |
|---|---|
| `Projection<'TEvents>` | The Projection type. Only a declaration: Rosetta reads its attributes and interfaces, and never creates it. |
| `[<HandlesStream("…")>]` | The stream it subscribes to: a category stream (`$ce-People`) or a [Custom Projection](custom-projections.md) stream (`cp-…`). |
| `IAmHandledBy<'THandler, 'TEvents>` | One per Handler, in the order they run. `'TEvents` must be the Projection's own DU: another doesn't compile (FS0366 "No implementation was given for 'abstract IProjectionOf.EventsType…'"). |
| `IProjectInto<'TReadModel>` | One per read model it writes. Used to [rebuild](#rebuilding-a-projection) it, to check PostgreSQL tables at startup, and to make sure a read model has one owner. |
| `IProjectionHandler<'TEvents>` | A Handler: `Handle(event, checkpoint)`. Resolved from DI as a transient service, so it can take any registered service. |
| `INoSqlStore` / `ISqlStore` | The read model store the Handler takes: documents (RavenDB, MongoDB) or tables (PostgreSQL). |

Other attributes on the Projection type:

| Attribute | Effect |
|---|---|
| `[<ProjectionVersion(n)>]` | The read models' version: bump it to [rebuild](#rebuilding-a-projection) them. None = 0. |
| `[<RebuildWithoutClearing>]` | A [rebuild](#rebuilding-a-projection) empties nothing: every Handler overwrites whole documents or rows. Lets a Projection that can be rebuilt declare no `IProjectInto`. |
| `[<SkipUnknownEvents>]` | An event its DU has no case for is logged and skipped instead of stopping the Projection. See [Event evolution](event-evolution.md#unknown-events). |
| `[<DisabledProjection>]` | The Projection doesn't run, even when registered by name. |

The **Projection's name** is its type name without "Projection" (`PersonProjection` → `Person`). It names the
checkpoint (`Checkpoints-Person`), the logs, the traces, the metrics and the health check data.

## The store API

`INoSqlStore` and `ISqlStore` are both `IProjectionsStore`:

| Member | |
|---|---|
| `StoreAsync<'T>(doc)` | Upserts the read model by its key. |
| `StoreManyAsync<'T>(docs: 'T[])` | Upserts several. |
| `LoadAsync<'T>(id: obj)` | The read model with that key, or `null` (check with `isNull (box x)`). |
| `LoadManyAsync<'T>(ids: obj[])` | A `Dictionary<obj, 'T>` of every id asked for; missing ones map to `null`. |
| `DeleteAsync<'T>(id: obj)` | Deletes by key. |
| `DeleteManyAsync<'T>(ids: obj[])` | Deletes several. |

- **The key** differs per store:
    - **RavenDB and MongoDB** key a document by its `Id`, as a string (`Id.ToString()`): give document read models a
      string `Id`. `[<Key>]` and composite keys aren't used, and RavenDB's `LoadManyAsync` returns the string ids as
      the dictionary keys.
    - **PostgreSQL** keys a row by its `Id`, or by its `[<Key>]` properties in order for a composite key, passed as a
      tuple: `LoadAsync<AreaHour>((areaId, hour))`.
    - The **Projections TestKit** keys like PostgreSQL, so a composite-key *document* read model passes its tests and
      then fails on a real document store: keep document read models on a single string `Id`.
- Inside a Handler, every call is in the event's transaction (below), so there's no unit of work to manage. Outside a
  Projection (an API, say), the stores write straight away: each `StoreAsync` on its own, each
  `StoreManyAsync`/`DeleteManyAsync` all or nothing.
- **Document read models** (RavenDB, MongoDB): F# records with `[<CLIMutable>]`, or classes with settable properties.
  RavenDB uses the `Id` as the document id and its conventions for the collection; MongoDB stores them in the
  pluralized type name collection (`Person` → `People`).
- **An update is load, change, store.** Load first and skip (or create) when the read model is missing. In RavenDB,
  storing a new object under a loaded document's `Id` (a record copied with `with`) replaces it.

## Projections at runtime

### One transaction per event

- The Projection's Handlers run **one at a time, in the order the Projection declares them** (`IAmHandledBy`), inside
  one transaction, so a Handler sees what the ones before it wrote. Then the event's checkpoint is written in the same
  transaction, and it's committed: one session's `SaveChanges` in RavenDB, a transaction in MongoDB or PostgreSQL.
- When a Handler fails, the event stops there: the `ProjectionException` holds that failure, and **nothing is
  written**, neither the earlier Handlers' writes nor the checkpoint. The event is handled again, by every Handler, from
  the read models as they were. So a read model that adds one for each event (analytics, totals) counts each event
  once, however often it's retried.
- A commit that failed may still have been written (its reply lost): before the event is handled again, the stored
  checkpoint is read, and an event it already includes isn't handled twice.
- Effects outside the read models (a command to an Aggregate, a message, an HTTP call) aren't in the transaction: they
  run before the commit, and again when the event is retried, so they must be **idempotent, and quick** (the
  transaction stays open meanwhile).
- **MongoDB needs a replica set** for the transactions (a single node is enough). On a standalone server the
  Projections don't start, with an error that says how to set one up.

### Checkpoints

- Each Projection stores its position as `Checkpoints-{Name}`, **one-based** (the number of events handled), with its
  `ProjectionVersion`. A restarted service continues where it stopped.
- They live in the read model store: a RavenDB document, a MongoDB document, or a row of `rosetta.checkpoints` in
  PostgreSQL.

### Failures never stop the application

- Projections run in a `BackgroundService`. The application finishes starting right away, and stopping it cancels
  every subscription immediately.
- When a Projection fails (KurrentDB or the read store unavailable, a Handler that throws), it resubscribes from its
  last checkpoint, waiting **1, 2, 4, 8, 16 s, then 30 s** between attempts, for as long as it takes; the other
  Projections keep running. Only progress resets the count: a handled event, or catching up with the stream.
- After **5 failures in a row** without progress (usually the same event failing: a bug), it's **Faulted**: logged as
  Critical once and reported Unhealthy, and it keeps retrying every 30 s, so a fixed deployment picks it up. When it
  handles events again, it logs that it recovered.
- An event the Projection's DU has no case for **stops** that Projection at once, since retrying can't help (unless
  it's `[<SkipUnknownEvents>]`). So does a startup error that's the same on every attempt: a configuration mistake,
  MongoDB without a replica set, or a `ProjectionVersion` older than its read models. A startup error that can heal (a
  database still booting, a PostgreSQL table your migrations haven't created yet) is retried like a failure.

| State | Means | Health |
|---|---|---|
| Starting | preparing its store and checkpoint | Degraded |
| Running | subscribed and handling events | Healthy |
| Retrying | failed, waiting to resubscribe | Degraded |
| Faulted | 5 failures in a row; still retrying every 30 s | Unhealthy |
| Stopped | an unknown event or a permanent startup error; not retrying | Unhealthy |

### Health and alerting

```fsharp
builder.Services.AddHealthChecks().AddRosettaProjections() |> ignore   // "rosetta-projections"
// or with a name and tags of your own:
builder.Services.AddHealthChecks().AddRosettaProjections("projections", [ "ready" ]) |> ignore
```

- **Healthy** when every Projection runs, **Degraded** while one starts or retries, **Unhealthy** when one is Faulted or
  Stopped. The data also has an entry "KurrentDB custom projections" while those are being deployed. Its data has each Projection's state, stream, checkpoint, failures in a row, last error and since when.
- For metrics-based alerting, the gauge `rosetta.projections.unhealthy` reports 1 for each Projection that is Faulted
  or Stopped and 0 for the others (tags `rosetta.projection`, `rosetta.state`): alert when its sum is above 0. The
  counter `rosetta.projection.faults` counts faults and stops as they happen. See [Telemetry](../reference/telemetry.md).

### Stopping the application instead

For a process that only runs Projections under an orchestrator that restarts it, add `StopApplicationOnFailure()`:

```fsharp
builder.UseProjectionsWith<KurrentDB, MongoDB>(fun projections ->
    projections.FromAssemblyOf<PersonProjection>()
    projections.StopApplicationOnFailure()) |> ignore
```

A Projection that faults, stops or can't start then stops the application, logged as Critical, with
`Environment.ExitCode` 1. A C# `void Main` exits with it on its own; an F# `main` must return it, or the process exits
with 0 and restart policies won't notice:

```fsharp
host.Run()
Environment.ExitCode
```

## Rebuilding a projection

When a Projection's Handlers change what they write, bump its version: on the next start, Rosetta empties its
`IProjectInto` read models (RavenDB/MongoDB collections deleted by query, PostgreSQL tables truncated), resets its
checkpoint, and the Projection projects its stream again from the start.

```fsharp
[<HandlesStream("$ce-People"); ProjectionVersion(2)>]
type PersonProjection() =
    inherit Projection<PersonEvents>()
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents>
    interface IProjectInto<Person>
```

- Without the attribute the version is 0, so the first `ProjectionVersion(1)` rebuilds too.
- A Projection of a [Custom Projection](custom-projections.md#the-projections-that-read-it)'s `cp-` stream is also
  rebuilt, without a version bump, when the stream was linked again from the start (its definition changed) or replaced
  (renamed, deleted): its checkpoint is then in links that are gone, or past the stream's end. It logs a Warning,
  "Projection {Name} is REBUILT: {reason}. …".
- The read models are emptied before the checkpoint is reset, so a crash in between empties them again.
- A Projection that can be rebuilt (a `ProjectionVersion` above 0, or a `cp-` stream) must declare its read models with
  `IProjectInto`: replaying its stream over read models nothing emptied would apply every event twice, so registering
  it fails ("Projection {P} can be rebuilt (…), but declares no read models to empty first …"), and so does starting
  one created without registration. `[<RebuildWithoutClearing>]` allows it when every Handler only overwrites whole
  documents or rows, so a replay gives what a fresh start would.
- A read model belongs to one Projection: two that declare the same one fail at registration, so rebuilding one never
  empties the other's.
- Until it has caught up, a rebuilt Projection's read models are partial.
- A Projection whose version is lower than its stored one (an older deployment still running) doesn't start.

## Projections with several Handlers

```fsharp
[<HandlesStream(ProjectionStreamNames.PersonStream)>]
type PersonProjection() =
    inherit Projection<PersonEvents>()
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents>          // runs first
    interface IAmHandledBy<PersonStatisticsHandler, PersonEvents>          // then this one, same transaction
    interface IProjectInto<Person>
    interface IProjectInto<PersonStatistics>
```

Use several Handlers to keep each read model's code apart, or to run an effect (a command, a message) after the read
models are written. All Handlers of a Projection must use the same store kind (see [PostgreSQL](postgresql.md#one-store-per-projection)).

## Choosing the store

| Documents (RavenDB, MongoDB) | Tables (PostgreSQL) |
|---|---|
| views read whole, the way a screen shows them | facts you'll aggregate, filter and join with SQL |
| nested data, lists, varied shapes | flat rows, analytics, reports, exports |
| schema-free: a new field just appears | your migrations own the schema |

One application can have both: `UseProjectionsWith<KurrentDB, RavenDB, PostgreSQL>`. Each Projection writes to the
store its Handlers take. See [Registration](registration.md).
