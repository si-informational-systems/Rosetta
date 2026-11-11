---
title: Troubleshooting
description: Rosetta's error messages, what causes them, and how to fix them.
---

# Troubleshooting

Messages are quoted as Rosetta writes them; `{…}` stands for the names in your code.

## At build time

**FS0366 "No implementation was given for 'abstract IProjectionOf.EventsType…'"** on an `IAmHandledBy` line
: The Handler handles another events DU than the Projection's. Use `IAmHandledBy<Handler, TheProjectionsEvents>` with a
  Handler of `IProjectionHandler<TheProjectionsEvents>`.

**`this.Apply event` doesn't compile**
: `Apply` only takes the events DU of the Aggregate's state (`AggregateState<'TEvents>`). The event is from another DU.

**A store marker type argument doesn't compile** (`UseAggregatesWith<MyStore, …>`)
: The stores are a closed set: only Rosetta's `KurrentDB`, `RavenDB`, `MongoDB` and `PostgreSQL` markers are accepted,
  each in the positions it fits.

**CS0118 "'KurrentDB' is a namespace but is used like a type"** (C#)
: The marker types collide with the `KurrentDB.Client` and `MongoDB.Driver` namespaces. Alias the namespace:
  `using Stores = SI.Rosetta.Aggregates.HostBuilder;` and write `Stores.KurrentDB`. See [C#](csharp.md).

**`KurrentDB` (or `RavenDB`) is ambiguous** (F#)
: Both `SI.Rosetta.Aggregates.HostBuilder` and `SI.Rosetta.Projections.HostBuilder` define the markers. Qualify them in a
  host that opens both.

## At registration (startup)

**"{Handler} can't be registered: it needs a marker interface that inherits IAggregateHandler (e.g. I{Handler}), which it's registered under and resolved by."**
: Add `type I{X}AggregateHandler = inherit IAggregateHandler` and `interface I{X}AggregateHandler` on the handler.

**"KurrentDB:ConnectionString is missing from the configuration."** / **"PostgreSQL:ConnectionString is missing from the configuration (…)"**
: Add the section (see [Configuration](../getting-started/configuration.md)). For the samples, the config file must be
  in the output folder or overridden on the command line.

**`NullReferenceException` while registering RavenDB or MongoDB**
: `RavenDB:Urls` (or `MongoDB:Host`/`ConnectionString`) is missing.

**Startup hangs or fails on RavenDB**
: The `IDocumentStore` connects (and creates the database) at registration: RavenDB must be reachable when the service
  starts.

**"Event-sourced aggregates can only be added once: …"**, **"The outbox can only be added once: …"**, **"Projections can only be added once: …"**
: Each registration runs once per service collection. Name everything in one call, or use one assembly scan.

**"{ReadModel} is a read model of {A} and {B}: a read model belongs to one projection (IProjectInto), …"**
: Two Projections declare the same read model. Give each its own, or merge them.

**"Outbox:{key} must be a positive number of minutes, not '{value}'."**, **"The outbox's … must be …"**
: Fix the `Outbox` section or the `configure` function.

## When a command runs

**`DomainException` named `{Aggregate}DoesNotExist`**
: An update command (`IdempotentlyUpdateAggregate`) for an Aggregate that doesn't exist. Check the id (it's the stream
  name, e.g. `People-1`), or route the command to `IdempotentlyCreateAggregate` if it may create.

**`ConcurrencyException`**
: Another command appended to the same Aggregate between this one's load and store (KurrentDB), or created or changed
  its stored state (state-based, in any store: "{State} {id} already exists: another command created it after this one
  found it missing." / "{State} {id} is no longer at version {n}: another command changed it after this one loaded
  it."). Run the command again with a fresh handler; see [Recipes](recipes.md#handle-commands-from-a-message-bus).

**`ConcurrencyException` from the RavenDB Outbox ("… was recorded in the outbox by another command meanwhile …")**
: A concurrent repeat of the same command recorded the same event first. Run the command again; it's idempotent.
  RavenDB's own exception is its `InnerException`.

**`UnknownEventException`: "Event {Name} (event {n} of stream {stream}) has no case in {Events}. Add the case or upcast the event with an IEventUpcaster; a projection can skip it with [<SkipUnknownEvents>]."**
: An Aggregate's stream holds an event its DU doesn't know: a case was removed or renamed without `[<EventType>]`. Add
  the case back, give the renamed record `[<EventType("OldName")>]`, or upcast it. An Aggregate can't skip it.

**"The table {schema.table} (for {State}) doesn't exist: create it before the aggregates are stored."**, **"… has no column {column (Property)}."**, **"… has no primary key or unique index on (id): rows are written by their key."**
: An `InvalidOperationException` on every command of a state-based Aggregate in PostgreSQL: its table doesn't match
  the state type. Fix the migration; once it has run, the next command checks the table again and goes through.

**"{State} is built from {Events}, so it can't apply {Type}."**
: An event of another DU reached the state, e.g. two Aggregates writing to the same stream id. Keep ids unique per
  category.

**"{Events} has more than one case stored under the same event type name: {names}. Give their records different [<EventType>] names."**
: Two cases of one DU share a stored name (the same record type, or the same `[<EventType>]`). Each case needs its own.

**The handler resolves but `GetPublishedEvents()` has another command's events**
: A handler instance was reused across commands. Resolve one per command (they're transient).

## When a Projection runs

**The Projection is Faulted** (Critical "FAULTED", health Unhealthy)
: Five failures in a row without progress. The log and the health check's `lastError` show the
  `ProjectionException`: "PROJECTION FAILURE: Projection {Name} on Stream {Stream} FAILED on Checkpoint {n} during
  {Event}", with the Handler's exception inside. Fix the Handler (or the data) and deploy: it keeps retrying every
  30 s and recovers by itself.

**The Projection is Stopped after an unknown event**
: Its DU has no case for an event in the stream: deploy the version that handles it, or mark the Projection
  `[<SkipUnknownEvents>]` if it doesn't care.

**"MongoDB projections need a replica set: …"**
: Start MongoDB as a replica set (one node is enough); see [Operations](operations.md#mongodb).

**"The table {schema.table} (for {Type}) doesn't exist: create it before the projections start."**, **"… has no column {column (Field)}"**, **"… has no primary key or unique index on ({keys}): rows are written by their key."**
: The PostgreSQL table doesn't match the read model. Fix the migration; the Projection retries until it matches.

**"{Type}'s key has {n} values ({names}), but {m} were given."**
: A composite key was loaded or deleted with the wrong tuple. Pass every `[<Key>]` part, in declaration order.

**"{Type} has no key: give it an Id, or mark its key properties with [<Key>]."**
: A read model needs an `Id` (or `[<Key>]` properties).

**"Projection {P} is at version {v}, but its read models were built by version {stored}: a newer version of it runs elsewhere."**
: An older deployment started after a newer one rebuilt the read models. Stop the older one.

**"Type {T} must have HandlesStream attribute"**
: Add `[<HandlesStream("…")>]` (exactly one) to the Projection.

**"Projection {P} has handlers that write to PostgreSQL (ISqlStore) and to RavenDB or MongoDB (INoSqlStore)… Split it into two projections."**
: A transaction can't span two databases: split the Projection.

**"Projection {P} has no checkpoint store: register a projections store."**
: The Projections were registered without a store (custom wiring). Use `UseProjectionsWith<KurrentDB, …>`.

**A read model is empty, but the stream has events**
: Check that `$by_category` runs in KurrentDB (the `$ce-` stream exists), that the Projection isn't
  `[<DisabledProjection>]`, that it's in the scanned assembly (or named), and the health check's state.

**A composite-key read model passes the TestKit but fails in RavenDB/MongoDB**
: Document stores key by `Id` only. Give document read models a single string `Id`.

## Custom Projections

**"KurrentDB Custom Projection Stream name must start with 'cp-'. …"**
: Rename the stream constant.

**"Stream constant '{X}Stream' not found in assembly {A}"**
: Add a `string` literal `{X}Stream` (e.g. `PersonStream = "$ce-People"`) to the assembly of the Projection that
  handles the Custom Projection's stream.

**The `cp-` stream stays empty**
: KurrentDB's projections must be enabled. Check the projection in the KurrentDB UI: its status, and that emit is on.
  Rosetta re-submits a Faulted one on the next start.

**"Custom projection {Name} UPDATED without comparing, its definition couldn't be read"**
: The KurrentDB HTTP API wasn't reachable with the connection string's address and credentials. Harmless: the
  definition was re-submitted.

## Tests

**"[TEST INVALID] Id field must be present on the Command!"**
: The Aggregates TestKit needs an `Id: string` on every command record.

**"[TEST INVALID]: Then or ThenError were not called!"** / **"[TEST INVALID]: Then was not called!"**
: Every test must end with a `Then…`.

**"[TEST FAILED] Test did not fail on error: {name} as was expected"**
: The command didn't raise `DomainException.Named(name, …)`. A plain `DomainException("…")` has no name to match.

**`MissingMethodException` building the handler in the Aggregates TestKit**
: The handler must have one constructor taking a single repository argument.

**"Key value type not supported! …"** (Projections TestKit)
: Keys must be string, int, int64, Guid, DateTime, DateTimeOffset, DateOnly or an enum.

**`dotnet test` finds no tests**
: xUnit v3 needs `<OutputType>Exe</OutputType>` and, on the .NET 10+ SDK, `"test": { "runner": "Microsoft.Testing.Platform" }`
  in `global.json`.
