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

**"Projection {P} can be rebuilt ({reason}), but declares no read models to empty first: …"**
: It has a `[<ProjectionVersion>]` above 0 or reads a `cp-` stream, so it can be rebuilt by replaying its stream, and
  nothing would be emptied first. Declare the read models it writes with `IProjectInto<…>`, or mark it
  `[<RebuildWithoutClearing>]` if every Handler overwrites whole documents or rows (no `+1`, no appending). Raised at
  registration, or when a Projection created without it is rebuilt (then it doesn't start, and its checkpoint stays).

**"Outbox:{key} must be a positive number of minutes, not '{value}'."**, **"Outbox:{key} must be a positive whole number, not '{value}'."**, **"The outbox's … must be …"**
: Fix the `Outbox` section or the `configure` function.

**"The outbox's publisher can only be added once: …"**
: `UseOutboxPublisher`/`AddOutboxPublisher` runs once per service collection, with one publisher.

**"The outbox's publisher needs the outbox: add it with `UseOutboxWith` or `AddOutboxWith`."** (when the host starts)
: `UseOutboxPublisher` was registered without the Outbox. Add `UseOutboxWith<…>`, before or after it.

**"The outbox ({Type}) can't be published by UseOutboxPublisher: it doesn't implement IOutboxRelayStore. …"**
: A custom `IOutbox` is registered. Implement `IOutboxRelayStore` on it too, or publish it yourself.

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

**"{State} can't be snapshotted: a snapshot would lose {names}. Keep its state in public properties with public setters (member val X = … with get, set)."**
: An `InvalidOperationException` on the first load of a state with `[<Snapshot>]`: it keeps data in fields
  (`let mutable`) or properties with private setters, which the snapshot's JSON wouldn't restore. Make them public
  `member val … with get, set`, or remove `[<Snapshot>]`.

**Warning "The snapshot of {State} {id} at version {n} couldn't be written: …"**
: The command stood; only the snapshot failed (KurrentDB unreachable meanwhile, say). The Aggregate loads from an older
  snapshot, or its whole stream, until the next one is due.

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

## When the Outbox publishes

**"The outbox {Collection} FAILED to publish. Retrying in {Delay} …"**, Critical **"… FAILED to publish 5 times in a row. …"**
: Your `IOutboxPublisher` threw (the broker down, a refused message), or the Outbox's store failed. The events before
  the failing one were marked; it's retried from there for as long as it takes, and the later events wait. Fix the
  cause; "RECOVERED" is logged when it publishes again. An event the broker will never take holds up the Outbox: fix
  the publisher (e.g. route it elsewhere) rather than letting it throw forever.

**Nothing is published, and "… is published by another instance: this one ({Owner}) waits to take over."**
: Another instance holds the lease. One that stopped hands it over; one that crashed holds it until it expires (30 s),
  then this one takes over.

**Events recorded before the publisher was registered aren't published** (RavenDB, MongoDB)
: Expected: a record without `PublishedAt` is taken to be your own former publisher's. Publish those yourself, or set
  their `PublishedAt` to `null` to hand them to the publisher.

**Old events are published again after registering the publisher** (PostgreSQL)
: The `published_at` column was added to the existing table, so every row there is unpublished. Delete the rows your
  own publisher already sent before registering the built-in one.

**Consumers receive an event twice**
: Delivery is at least once (a crash between publishing and marking, or a lease handed over mid-batch). Dedupe on the
  message id, the event's `Id`.

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

**"Projection {P} couldn't be deleted: DELETE … returned …"**
: Removing a Custom Projection the code no longer defines goes through the KurrentDB HTTP API, which refused or wasn't
  reachable. The Custom Projections are retried as a startup failure until it succeeds (or, when the application
  defines none anymore, logged as a Warning and tried again on the next start). Check the connection string's address
  and credentials.

**"Projection {Name} is REBUILT: {stream} was linked again from the start: …"** (or "… is shorter than checkpoint …", "… doesn't exist, but …")
: Its Custom Projection's definition changed (relinked), or its `cp-` stream was renamed or deleted. Expected: its
  `IProjectInto` read models are emptied and projected again from the stream.

**A Custom Projection that was renamed or removed long ago still runs**
: Rosetta deletes only the Custom Projections its application recorded as its own, starting with the first start that
  keeps the record. Delete older ones by hand (see [Operations](operations.md#kurrentdb)).

**A Projection of a `cp-` stream counts the stream's events twice after a deployment**
: It ran in another service while the Custom Projection was relinked. Keep it in the application that defines the
  Custom Projection, or bump its `[<ProjectionVersion>]` to rebuild it.

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
