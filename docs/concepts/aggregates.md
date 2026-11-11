---
title: Aggregates
description: State, Aggregate and Aggregate Handler — how commands become events, and how Aggregates are stored.
---

# Aggregates

An Aggregate is three types:

| Type | Inherits | Does |
|---|---|---|
| **State** | `AggregateState<'TEvents>` | Holds what the Aggregate knows, and changes only by applying events (`ApplyEvent`). |
| **Aggregate** | `Aggregate<'TState, 'TCommands>` | Decides: validates a command against the state and applies the events it causes (`Execute`). |
| **Aggregate Handler** | `AggregateHandler<'TAggregate, 'TCommands>` | Routes each command to create or update, and is what the application calls. |

## State

```fsharp
open SI.Rosetta.Aggregates

type PersonAggregateState() =
    inherit AggregateState<PersonEvents>()
    member val Name = "" with get, set

    override this.ApplyEvent(ev) =
        match ev with
        | PersonEvents.Registered ev ->
            this.Id <- ev.Id
            this.Name <- ev.Name
        | PersonEvents.NameChanged ev ->
            this.Name <- ev.NewName
```

- `Id` and `Version` come from the base class. `Version` is the number of events applied (0 = doesn't exist yet).
- `ApplyEvent` only assigns. No validation, no exceptions: events are facts, and replaying old events must always
  work. Set `Id` in the creating event.
- State-based Aggregates are stored as this object (a RavenDB or MongoDB document, or a row of a PostgreSQL table), and
  so are [snapshots](#snapshots), so keep its properties serializable: `member val … with get, set`.

## Aggregate

```fsharp
type PersonAggregate() =
    inherit Aggregate<PersonAggregateState, PersonCommands>()

    override this.Execute(command) =
        match command with
        | PersonCommands.Register cmd ->
            if this.State.Version > 0 then
                if this.State.Name <> cmd.Name then
                    raise (DomainException.Named("PersonAlreadyRegistered", "Person already registered"))
                // else: an idempotent repeat, nothing to do
            else this.Register cmd
        | PersonCommands.ChangeName cmd ->
            if this.State.Name <> cmd.Name then this.ChangeName cmd

    member private this.Register(cmd: RegisterPerson) =
        let event = PersonEvents.Registered { Id = cmd.Id; Name = cmd.Name }
        this.PublishedEvents.Add event   // optional: an integration event (see Outbox)
        this.Apply event

    member private this.ChangeName(cmd: ChangePersonName) =
        this.Apply(PersonEvents.NameChanged { Id = cmd.Id; OldName = this.State.Name; NewName = cmd.Name })
```

- `this.Apply event` applies the event to the state (`Version + 1`) and adds it to `Changes`, the events to store. It
  only compiles with the state's events DU.
- `this.PublishedEvents.Add event` also makes it an integration event. Applying and publishing are independent: an
  event can be stored and not published, or (for an idempotent repeat) published again without being applied.
- `this.State.Version > 0` (also `this.ShouldHandleIdempotency`) tells a create command that the Aggregate exists.

### Idempotency

Commands are redelivered (retries, queues), so every command must be safe to repeat. Rosetta doesn't decide what a
repeat is: the Aggregate compares the command with its state and applies nothing when the state already reflects it.
A command that applies nothing changes no `Version`, so nothing is stored.

| Situation | Do |
|---|---|
| Create, and the Aggregate exists with the same data | nothing (idempotent) |
| Create, and it exists with different data | `raise (DomainException.Named("XAlreadyExists", …))` |
| Update that changes nothing | nothing |
| Update that breaks a rule | `raise (DomainException.Named("…", …))` |

With the [Outbox](outbox.md), re-publish the integration events in the idempotent branch too, so a crash between
storing and recording is healed by the redelivered command.

### Business errors

Raise `DomainException.Named(name, message)`. `Name` is the stable identifier callers match on (an API maps it to a
4xx response, a test uses `ThenError(name)`); `message` is for humans. A plain `DomainException("message")` has an
empty `Name` and can't be matched.

## Aggregate Handler

```fsharp
type IPersonAggregateHandler =
    inherit IAggregateHandler

type PersonAggregateHandler(repo: IEventSourcedAggregateRepository) =
    inherit AggregateHandler<PersonAggregate, PersonCommands>()
    do base.AggregateRepository <- repo
    interface IPersonAggregateHandler

    override this.ExecuteAsync(command) =
        task {
            match command with
            | PersonCommands.Register cmd -> do! this.IdempotentlyCreateAggregate cmd.Id command
            | PersonCommands.ChangeName cmd -> do! this.IdempotentlyUpdateAggregate cmd.Id command
        }
```

- **The marker interface is required.** The handler is registered in DI as transient under its first interface whose
  name isn't `IAggregateHandler`. Without one, registration fails.
- **The constructor takes one repository**: `IEventSourcedAggregateRepository` (KurrentDB) or
  `IStateBasedAggregateRepository` (RavenDB/MongoDB). A plain `IAggregateRepository` isn't registered and won't resolve.
  The Aggregates TestKit also requires exactly one constructor argument.
- `IdempotentlyCreateAggregate id command` loads the Aggregate (a new one when missing), executes the command and stores
  it when its `Version` changed.
- `IdempotentlyUpdateAggregate id command` does the same, but raises `DomainException.Named("{Aggregate}DoesNotExist")`
  (e.g. `PersonDoesNotExist`) when the Aggregate is missing. The name is the Aggregate type's name without "Aggregate".
- Each command is traced and measured (see [Observability](observability.md)).

### Calling it

```fsharp
type PeopleEndpoints(people: IPersonAggregateHandler) =
    member this.Register(cmd: RegisterPerson) =
        task {
            try
                do! people.ExecuteAsync(PersonCommands.Register cmd)
                let published = people.GetPublishedEvents()   // the integration events of this command
                return Ok published
            with
            | :? DomainException as ex -> return Error ex.Name           // a rule said no: 4xx
            | :? ConcurrencyException -> return Error "Conflict"          // another write won: retry the command
        }
```

`ExecuteAsync` takes the commands DU (`IAggregateCommands`). A handler instance holds the last command's
`PublishedEvents`, so resolve it per request or message (it's transient).

## Storage

The Aggregate's handler constructor decides how it's stored.

### Event-Sourced: KurrentDB

- The **stream name is the Aggregate's `Id`**. Use `{Category}-{id}` ids (`People-1`, `People-6f1c…`) so KurrentDB's
  `$ce-People` category stream collects every Person's events for [Projections](projections.md).
- Loading reads the stream and applies each event to a new state (through the registered
  [upcasters](event-evolution.md#upcasters)). An event the DU doesn't know fails the load (`UnknownEventException`):
  rebuilding state without it would be wrong.
- Storing appends `Changes` expecting the version that was loaded: **optimistic concurrency**. When another write got
  there first, it throws `ConcurrencyException`; run the command again (it reloads the state).
- Event data is the record as JSON; metadata carries the stored name, CLR type names and the trace context. See
  [Stored format](../reference/stored-format.md).

#### Snapshots

A long-lived Aggregate (thousands of events) loads faster from a snapshot of its state. Opt in on the state type:

```fsharp
[<Snapshot(100)>]   // every 100 events; [<Snapshot(100, Version = 2)>] ignores the snapshots taken before
type AccountAggregateState() =
    inherit AggregateState<AccountEvents>()
    member val Balance = 0m with get, set
    member val Overdrawn = false with get, set
    override this.ApplyEvent(ev) = …
```

- **Taken** after the command's events are appended, whenever the command takes the Aggregate's `Version` to or past a multiple of `Every`,
  into the stream `RosettaSnapshots-{Id}`: its own category, so no `$ce-…` stream and no Projection sees it. Only the
  latest is kept (`$maxCount` 1). A snapshot that fails to be written is logged as a Warning; the command stands.
- **Loaded**: the latest snapshot, then only the events after it (through the upcasters). It's ignored, and the whole
  stream read, when it no longer fits: the state's public settable properties (names and types) or the `[<Snapshot>]`
  `Version` changed, or the event it ends with isn't in the stream where it was (a deleted or rewritten stream).
  Optimistic concurrency is unchanged.
- **Raise `Version`** when `ApplyEvent` changes what it builds from the same events: the old snapshots would hold what
  the old code built.
- **The state must round-trip as JSON** (`EventEncoding.JsonOptions`): public properties with public setters
  (`member val X = … with get, set`). A `let mutable` field or a private setter would be lost, so the first load of the
  state type fails with `InvalidOperationException`: "{State} can't be snapshotted: a snapshot would lose {names}. …".
  Computed get-only properties are fine.
- Only for Aggregates with long streams: a short stream loads as fast without, and every snapshot is one more write.
  `KurrentDBAggregateRepository.SnapshotStreamOf id` names the stream.

### State-based: RavenDB, MongoDB or PostgreSQL

| Store | Where the state lives | Where `Version` lives |
|---|---|---|
| RavenDB | the state object as a document, id = `Id` | the document's `@metadata` (`Rosetta-Aggregate-Version`) |
| MongoDB | the state object as a document in the pluralized state type name collection, `_id` = `Id` | a regular `version` field |
| PostgreSQL | a row of your table, the snake_case plural of the state type name (`PersonAggregateState` → `person_aggregate_states`), a column per property, key `id` | a regular `version` column |

- Only the latest state is kept: no history, no events in KurrentDB, so no Projections from these Aggregates.
- **Optimistic concurrency**, as with KurrentDB, in every store: a write never overwrites another command's. A new
  Aggregate is stored only while its `Id` doesn't exist, a loaded one only while the stored state is still at the
  version it was loaded at; otherwise `ConcurrencyException`, nothing is written (its Outbox events included), and the
  command can be run again (it reloads the state).

    | Store | New Aggregate | Loaded Aggregate |
    |---|---|---|
    | RavenDB | saved with an empty change vector (only while no document has the id) | the stored document's version (`@metadata`) is checked, then it's saved over that document's change vector, so RavenDB refuses it if it changed in between |
    | MongoDB | `InsertOne` (a duplicate key is a conflict) | `ReplaceOne` filtered on `_id` and `version` |
    | PostgreSQL | `insert … on conflict (id) do nothing` | `update … where id = … and version = …` |

- PostgreSQL tables are yours: your migrations create them, mapped exactly like
  [PostgreSQL read models](postgresql.md#state-based-aggregates), and the app can query them with SQL.

### Choosing

| Event-Sourced (KurrentDB) when | State-based (RavenDB, MongoDB, PostgreSQL) when |
|---|---|
| the history matters (audit, analytics, read models from it) | only the current state matters |
| other parts of the system react to its events through Projections | nothing projects its events |

One service can have both: register with `UseAggregatesWith<KurrentDB, RavenDB>` and give each handler the repository
it needs. See [Registration](registration.md).
