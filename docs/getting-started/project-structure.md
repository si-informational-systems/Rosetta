---
title: Project structure
description: How to lay out a solution built on Rosetta — projects, folders, names — as the applications built on it do.
---

# Project structure

Rosetta doesn't impose a layout, but its packages are split so that each layer depends only on what it needs. This is
the layout the applications built on Rosetta use.

## Projects

```text
src/
  X.PublishedLanguage            commands and events DUs, value types; one file per Aggregate      SI.Rosetta.Common
  X.Domain                       states, Aggregates, Aggregate Handlers, Guards.fs                 SI.Rosetta.Aggregates
    People/
      PersonAggregateState.fs
      PersonAggregate.fs
      PersonAggregateHandler.fs
  X.Domain.Bus                   message handlers that call the Aggregate Handlers, policies        SI.Rosetta.Aggregates
  X.Domain.Infrastructure        the Outbox publisher, infrastructure the domain host needs
  X.Domain.App                   host of the Aggregates: Program.fs + Composition.fs               SI.Rosetta.Aggregates.HostBuilder
  X.ReadModel                    read model types and query interfaces, no infrastructure
  X.ReadModel.Projections        StreamNames.fs, Projections and their Handlers                     SI.Rosetta.Projections
  X.ReadModel.Queries.RavenDB    queries and indexes over the read models
  X.Projections.App              host of the Projections                                            SI.Rosetta.Projections.HostBuilder
  X.WebApi                       the API: sends commands, reads read models
tests/
  X.TestData                     fixtures: ids, a fixed "now", actors
  X.Domain.UnitTests             one folder per Aggregate: <Topic>Tests.fs + <Aggregate>Data.fs     SI.Rosetta.Aggregates.TestKit
  X.Projections.UnitTests        Projection tests                                                   SI.Rosetta.Projections.TestKit
  X.IntegrationTests             every service in-process against Testcontainers
```

| Project | References | Never references |
|---|---|---|
| Published Language | `SI.Rosetta.Common` | any database client, the domain |
| Domain | Published Language, `SI.Rosetta.Aggregates` | read models, Projections, hosts |
| Projections | Published Language, read model types, `SI.Rosetta.Projections` | the domain |
| Hosts | everything they run, the HostBuilder packages | each other |

- **Separate hosts** for the Aggregates and the Projections when they scale or deploy differently. A Projection runs as
  a single consumer (its checkpoint), so scale Projections by splitting them across hosts, not by running copies.
- **`Composition.fs`** in each host: a `Configure (builder: IHostApplicationBuilder)` that registers everything, called by
  `Program.fs` and reused by the integration tests to run the service in-process.

## Names

| What | Name | Example |
|---|---|---|
| Commands DU | `{Aggregate}Commands`, cases are verbs | `PersonCommands.Register` |
| Command record | imperative | `RegisterPerson` |
| Events DU | `{Aggregate}Events`, cases are past participles | `PersonEvents.Registered` |
| Event record | past tense, with `[<EventType("same name")>]` | `PersonRegistered` |
| State | `{Aggregate}AggregateState` (event-sourced) | `PersonAggregateState` |
| Aggregate | `{Aggregate}Aggregate` — the Aggregate's name is this without "Aggregate" | `PersonAggregate` → `Person` |
| Handler + marker | `{Aggregate}AggregateHandler`, `I{Aggregate}AggregateHandler` | `IPersonAggregateHandler` |
| Ids and streams | `{PluralCategory}-{id}` | `People-42` |
| Stream constants | `{Aggregate}Stream` literals in the Projections assembly | `PersonStream = "$ce-People"` |
| Projection | `{Name}Projection` — its name is this without "Projection" | `PersonProjection` → `Person` |
| Projection Handler | `{Name}ProjectionHandler` | `PersonProjectionHandler` |
| Business error | PascalCase, specific | `PersonAlreadyRegistered`, `{Aggregate}DoesNotExist` |

!!! warning "State-based Aggregates: the state's type name is the collection's"
    MongoDB stores a state-based Aggregate in the pluralized name of its **state type**, and RavenDB uses its
    conventions on the state type. `PersonAggregateState` becomes `PersonAggregateStates`. If the collection should
    read `People`, name the state `Person`. PostgreSQL takes the snake_case plural (`person_aggregate_states`), or the
    name in the state's `[<Table("…")>]`.

!!! note "Stream constants for Custom Projections"
    A [Custom Projection](../concepts/custom-projections.md) finds each source Aggregate's category stream by a
    `string` literal named `{Aggregate}Stream` in the **assembly of the Projection**. Keep the constants in one
    `StreamNames.fs` per Projections assembly; a second Projections assembly needs its own copy.

## Files of an Aggregate

```text
X.Domain/Reservations/
  ReservationAggregateState.fs     the state and ApplyEvent
  ReservationAggregate.fs          Execute and the domain methods
  ReservationAggregateHandler.fs   the marker interface and the handler
```

F# compiles in file order: in the `.fsproj`, the Published Language types come first, then `Guards.fs`, then each
Aggregate's state, Aggregate and handler.

## Shared helpers worth having

Applications built on Rosetta write the same few helpers. They're short; see [Recipes](../guides/recipes.md):

- `Guards.fs`: `Reject`/`Ensure` over `DomainException.Named`.
- An `Emit` member on Aggregates that publish every event: `PublishedEvents.Add` + `Apply`.
- A command-handling wrapper on the message bus: a `DomainException` is an outcome, a `ConcurrencyException` is retried
  with a fresh handler.
- An Outbox publisher: a background service that sends the recorded events to the broker.
- Projection helpers: `ChangeAsync` (load, change, store; fail when missing).
- Test fixtures: `Both events` for "produced and published", a `{Aggregate}Data.fs` with default commands and events.
