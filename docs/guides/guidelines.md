---
title: Guidelines
description: How to design with Rosetta — domain, events, projections, read models, operations and tests.
---

# Guidelines

The rules Rosetta's design assumes, and the lessons of the applications built on it.

## Domain

- **Keep the business rules in the Aggregate** (`Execute` and its domain methods). Aggregate Handlers only route
  commands to create or update; APIs and bus handlers only build commands and map outcomes.
- **Aggregates are pure.** No clock, no I/O, no services: everything a rule needs is in the command (who issued it,
  when, the prices it was quoted). Tests then fix time in the command, and replays are deterministic.
- **`ApplyEvent` only assigns.** No checks, no exceptions: events are facts already decided, and replaying them must
  always succeed.
- **Make every command idempotent.** Compare it with the state; when the state already reflects it, apply nothing.
  A create command that finds the Aggregate with the same data is a no-op; with different data it's a named error.
- **Name every business error** with `DomainException.Named(name, message)`, PascalCase and specific
  (`ReservationAlreadyCancelled`), and assert each one in a test.
- **One Aggregate per command.** A command changes one Aggregate; what follows in others is a policy reacting to its
  events (a bus handler sending the next command), not a transaction across Aggregates.
- **Ask when the model is unclear.** If it isn't obvious which Aggregate owns a rule, or what the rule is, settle it
  with the domain experts before coding it.
- **Use the domain's words** for Aggregates, commands, events and errors, and keep them consistent across the API,
  the events and the read models.

## Events

- **Give every event record an `[<EventType>]`** with its current name from the start, so the record can be renamed.
- **Never rewrite stored events.** Add fields freely; rename records with `[<EventType>]`; anything else is an
  [upcaster](../concepts/event-evolution.md#upcasters).
- **Put what consumers need in the event.** A Projection reads only its own stream, so data from another Aggregate it
  needs travels in the event (copied from the command).
- **Deploy readers before writers**: the Projections (and other consumers) that read a new event before the
  Aggregates that write it.
- **Integration events are a contract.** What goes in `PublishedEvents` is read by other services: change it as
  carefully as a public API.

## Persistence

- **Event-source an Aggregate** when its history matters or others react to its events; **state-based** when only the
  current state matters. State-based Aggregates have no optimistic concurrency (last write wins) and feed no
  Projections.
- **Ids are `{PluralCategory}-{id}`**: they're the stream names, and the category feeds `$ce-{PluralCategory}`.
- **Retry `ConcurrencyException`** by running the command again with a fresh handler (it reloads the state).
- **Use the Outbox** for integration events, and publish them at least once with the Outbox `Id` as the message id.

## Projections

- **One Projection per purpose**, declaring every read model it writes with `IProjectInto<…>`. A read model has one
  owner; a document the API writes too isn't declared (a rebuild would delete it).
- **Handlers only write read models.** Any other effect (a command, a message, an HTTP call) runs again when the event
  is retried: make it idempotent and quick, or move it to a policy on the bus.
- **Load before updating, and decide about missing.** A missing document the stream itself creates is a bug: fail (the
  Projection retries and then reports Faulted). Skipping silently hides it.
- **Bump `[<ProjectionVersion>]`** whenever a Handler changes what it writes into existing read models.
- **Keep document read models on a string `Id`.** Composite keys work in PostgreSQL only.
- **Choose the store per read model**: documents for screens (read whole, by id or index), PostgreSQL tables for SQL
  analytics and reports.
- **Scale by splitting.** A Projection is a single consumer of its checkpoint: run each in one instance, and spread
  Projections over hosts.
- **Watch them**: the `rosetta-projections` health check, or an alert on `rosetta.projections.unhealthy > 0`. A Faulted
  Projection doesn't stop the application, so something must notice.
- **Skip unknown events only on purpose.** `[<SkipUnknownEvents>]` suits a Projection that cares about a few events
  of a busy stream; on one that should handle everything it hides a missing case.

## PostgreSQL read models

- **The app's migrations own the tables**, run before the Projections start. Rosetta checks them, it never creates
  or changes them.
- **Match the table to the record**: `not null` unless `option`, `timestamptz` for dates, `numeric` for `decimal`,
  `jsonb` for nested records; a primary key on exactly the key columns.
- **Only the Projection writes its tables**, and nothing outside them references them with foreign keys (a rebuild
  truncates them).
- **Projections store by key; queries do the SQL.** Aggregate, join and filter on the query side.

See [PostgreSQL read models](../concepts/postgresql.md#guidelines-for-postgresql-read-models) for the details.

## Testing

- **Every Aggregate** with the Aggregates TestKit: each command's change, its idempotent repeat (`ThenNoEvents`),
  and every named error (`ThenError`).
- **Every Projection** with the Projections TestKit (PostgreSQL ones too); **every upcaster** with the Upcasting
  TestKit.
- **A few integration tests** for what memory can't check: a PostgreSQL schema against its read models, a MongoDB
  replica set, Custom Projections in KurrentDB, the Outbox publisher.
- **Test data in one place**: default commands and events per Aggregate, fixed ids and time.

## Operations

- **Run KurrentDB with projections on** (`$by_category` for the category streams, and Custom Projections).
- **MongoDB as a replica set**, even a single node.
- **Return `Environment.ExitCode`** from an F# `main` when using `StopApplicationOnFailure()`.
- **Export telemetry** (`SI.Rosetta` source and meter) to see a request through to every read model it changes.
