---
title: For AI agents
description: How an AI coding agent should use this documentation, and the rules it must not break when writing code on Rosetta.
---

# For AI agents

This page is for AI coding agents (and people briefing them) working in an application built on Rosetta. Rosetta's
source isn't in that application's repository: this documentation is the reference.

## Read the documentation, not the decompiled packages

| URL | Contents |
|---|---|
| [`/llms.txt`](https://si-informational-systems.github.io/Rosetta/llms.txt) | an index of every page, with a one-line summary each |
| [`/llms-full.txt`](https://si-informational-systems.github.io/Rosetta/llms-full.txt) | **the whole documentation in one Markdown file**: fetch this once per task |
| any page + `index.md` | that page as Markdown, e.g. [`/concepts/projections/index.md`](https://si-informational-systems.github.io/Rosetta/concepts/projections/index.md) |

For a specific question, start from: [Recipes](guides/recipes.md) (how do I…),
[Troubleshooting](guides/troubleshooting.md) (an error message), [API reference](reference/api.md) (a signature),
[Configuration reference](reference/configuration.md) (a key or a derived name).

## Add this to the application's agent instructions

Paste into the application's `CLAUDE.md` / `AGENTS.md`, adjusting the version:

```markdown
## Rosetta (SI.Rosetta.* 1.36)

This app's domain and read models are built on Rosetta (F# DDD + Event Sourcing + CQRS projections).
Docs: https://si-informational-systems.github.io/Rosetta/ — fetch /llms-full.txt for the whole reference
before changing Aggregates, events, Projections or their registration. Follow its Guidelines; never
change a stored event's name or shape without an [<EventType>] or an upcaster.
```

## The rules

Breaking one of these compiles fine and fails at runtime, or corrupts data later.

**Messages**

1. Every command and event DU case wraps **exactly one record**; commands DUs implement `IAggregateCommands`, events
   DUs `IAggregateEvents`; use `[<RequireQualifiedAccess>]`.
2. An event is stored under its **record's** name (or `[<EventType("…")>]`), never the case name. Within a DU, stored
   names must be unique.
3. **Never rename an event record that has stored events** unless it keeps `[<EventType("OldName")>]`. Never remove a
   case whose events are stored (Aggregates can't load them). Add new event records with an `[<EventType>]`.
4. Field changes beyond adding/removing a field need an `IEventUpcaster`, registered in **every** service that reads the
   events, tested with `UpcastingTestKitBase`.

**Aggregates**

5. `ApplyEvent` only assigns state; it never validates or throws. It sets `Id` in the creating event.
6. Rules live in `Execute`; violations raise `DomainException.Named("SpecificName", "message")`, never a plain
   `DomainException("…")` or another exception type.
7. Every command is **idempotent**: compare with the state and apply nothing on a repeat.
8. Aggregates read no clock and do no I/O; time and actor come in the command.
9. Each handler has a marker interface `I{X}AggregateHandler` inheriting `IAggregateHandler`, and one constructor
   parameter: `IEventSourcedAggregateRepository` or `IStateBasedAggregateRepository` (not `IAggregateRepository`), with
   `do base.AggregateRepository <- repo`.
10. Create commands go to `IdempotentlyCreateAggregate`, all others to `IdempotentlyUpdateAggregate` (which raises
    `{Aggregate}DoesNotExist`).
11. Ids are `{PluralCategory}-{id}`: the id is the KurrentDB stream.
12. Resolve a fresh handler per command; retry `ConcurrencyException` with a fresh one.

**Projections**

13. A Projection inherits `Projection<'TEvents>`, has exactly one `[<HandlesStream("$ce-…")>]`, one
    `IAmHandledBy<Handler, 'TEvents>` per Handler (same DU), and one `IProjectInto<ReadModel>` per read model it owns.
14. Handlers write read models only through `INoSqlStore`/`ISqlStore`, never a raw database session (that escapes the
    event's transaction and makes retries double-apply). A Handler takes one store kind; never both.
15. Updates are load → change → store; check `isNull (box x)` after `LoadAsync`.
16. Side effects in Handlers (commands, messages, HTTP) must be idempotent: a failed event is handled again.
17. Changing what a Handler writes into existing read models requires bumping `[<ProjectionVersion(n)>]`.
18. Document read models (RavenDB/MongoDB) have a single string `Id`; composite `[<Key>]`s are PostgreSQL only.
19. PostgreSQL tables (read models, and the states of state-based Aggregates) are created by the app's migrations
    (Rosetta only checks them): a column per field, `not null` unless `option`, a primary key on exactly the key columns.
20. Custom Projection streams start with `cp-`; the Projections assembly has a `{Aggregate}Stream` literal for each
    source Aggregate.

**Tests**

21. Aggregates: `TestKitBase<Handler>`; `Given(events…)`, `When(command)`, then exactly one of `Then(events…)`,
    `Then(produced, published)`, `ThenNoEvents()`, `ThenError("Name")`. Commands need `Id: string`.
22. Projections: `TestKitBase<Projection, Handler, Events>`; `do! this.Given(…)`, `do! this.Then(expectedReadModel)`;
    override `ConfigureServices` for a Projection's other Handlers.
23. Test projects are xUnit v3 executables (`<OutputType>Exe</OutputType>`).

**Deployment**

24. A new event: deploy the Projections that read it before the Aggregates that write it.

## Where things are decided

| Question | Answer |
|---|---|
| Event-sourced or state-based? | [Aggregates → Choosing](concepts/aggregates.md#choosing) |
| Documents or PostgreSQL? | [Projections → Choosing the store](concepts/projections.md#choosing-the-store) |
| What does a failure do at runtime? | [Projections at runtime](concepts/projections.md#projections-at-runtime) |
| Which package goes where? | [Installation](getting-started/installation.md) |
| How is a name derived (stream, collection, table, checkpoint)? | [Configuration reference → Names](reference/configuration.md#names-rosetta-derives) |

When a domain question is open (which Aggregate owns a rule, what the rule is), ask the people who know the domain
rather than guessing.
