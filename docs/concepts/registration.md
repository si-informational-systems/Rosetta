---
title: Registration
description: Registering Aggregates, the Outbox and Projections on any .NET host, from F# or C#.
---

# Registration

The first generic argument chooses the Event-Sourced store, the second the store for state-based Aggregates (or Projections).

**F#**

```fsharp
// Aggregates service
open Microsoft.Extensions.Hosting
open SI.Rosetta.Aggregates.HostBuilder

Host.CreateDefaultBuilder(args)
|> fun builder -> UseAggregatesWith<KurrentDB, RavenDB> builder typeof<PersonAggregateHandler>.Assembly
|> fun builder -> UseOutboxWith<RavenDB> builder // the PublishedEvents, recorded in RavenDB (see Outbox)
|> fun builder -> builder.Build().Run()
```

```fsharp
// Projections service
open Microsoft.Extensions.Hosting
open SI.Rosetta.Projections.HostBuilder

Host.CreateDefaultBuilder(args)
|> fun builder -> UseProjectionsWith<KurrentDB, MongoDB> builder typeof<PersonProjection>.Assembly
|> fun builder -> builder.Build().Run()
```

> Both namespaces define the `KurrentDB`/`RavenDB`/`MongoDB`/`PostgreSQL` marker types. If one host registers both Aggregates and Projections, qualify them (e.g. `SI.Rosetta.Projections.HostBuilder.RavenDB`).

**C#**

```csharp
using Microsoft.Extensions.Hosting;
using SI.Rosetta.Aggregates.HostBuilder;            // extension methods
using Stores = SI.Rosetta.Aggregates.HostBuilder;   // store marker types

Host.CreateDefaultBuilder(args)
    .UseAggregatesWith<Stores.KurrentDB, Stores.MongoDB>(typeof(PersonAggregateHandler).Assembly)
    .UseOutboxWith<Stores.MongoDB>()   // the PublishedEvents, recorded in MongoDB (see Outbox)
    .Build()
    .Run();
```

> In C#, refer to the store marker types through an alias: unqualified `KurrentDB` and `MongoDB` resolve to the `KurrentDB.Client` and `MongoDB.Driver` root namespaces (CS0118).

**`Host.CreateApplicationBuilder` and `WebApplication.CreateBuilder`** (`IHostApplicationBuilder`) have the same `Use…` methods:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.UseAggregatesWith<Stores.KurrentDB, Stores.MongoDB>(typeof(PersonAggregateHandler).Assembly);
builder.Build().Run();
```

```fsharp
let builder = Host.CreateApplicationBuilder(args)
builder.UseProjectionsWith<KurrentDB, MongoDB>(typeof<PersonProjection>.Assembly) |> ignore
builder.Build().Run()
```

And on a plain `IServiceCollection`, the `Add…` methods take the configuration (in F#, also as curried functions: `AddAggregatesWith<…> services configuration assembly`):

```csharp
services.AddProjectionsWith<KurrentDB, RavenDB>(configuration, typeof(PersonProjection).Assembly);
```

| Registration | Use when | Stores |
|---|---|---|
| `UseAggregatesWithEventSourcing<TEventSourced>` | every Aggregate is Event-Sourced | `KurrentDB` |
| `UseAggregatesWithoutEventSourcing<TStateBased>` | every Aggregate persists its state | `RavenDB`, `MongoDB`, `PostgreSQL` |
| `UseAggregatesWith<TEventSourced, TStateBased>` | a mix of both | both of the above |
| `UseOutboxWith<TOutboxStore>` | the Aggregates' PublishedEvents are recorded in an [Outbox](outbox.md) | `RavenDB`, `MongoDB`, `PostgreSQL` |
| `UseOutboxPublisher<TPublisher>` | the Outbox's events are [published](outbox.md#publishing-the-events) with your `IOutboxPublisher` | the Outbox's |
| `UseProjectionsWith<TEventRepository, TProjectionsStore>` | Projections | `KurrentDB` → `RavenDB`, `MongoDB`, `PostgreSQL` |
| `UseProjectionsWith<TEventRepository, TNoSqlStore, TSqlStore>` | Projections into documents and tables at once | `KurrentDB` → `RavenDB` or `MongoDB`, and `PostgreSQL` |

Each `Use…` has an `Add…` counterpart for `IServiceCollection`, and each can be used once per service collection. The stores are a closed set: only Rosetta's own `KurrentDB`, `RavenDB`, `MongoDB` and `PostgreSQL` compile as store arguments (the marker types can't be inherited). Given an assembly, Rosetta finds the Aggregate Handlers, Projections and Projection Handlers in it and registers them.

## Naming the Handlers and Projections

Instead of the assembly, every `Use…`/`Add…` above (the Outbox's aside) also takes a function that names what to register, one by one, or the assemblies to find it in, or both:

```fsharp
builder.UseAggregatesWith<KurrentDB, RavenDB>(fun aggregates ->
    aggregates.Handler<PersonAggregateHandler>()                 // under its marker interface, IPersonAggregateHandler
    aggregates.Handler<OrganizationAggregateHandler>()) |> ignore

builder.UseProjectionsWith<KurrentDB, MongoDB>(fun projections ->
    projections.Projection<PersonProjection, PersonEvents>()      // with the Handlers its IAmHandledBy declare
    projections.FromAssemblyOf<OrganizationProjection>()) |> ignore   // and every Projection in that assembly
```

```csharp
builder.UseAggregatesWith<Stores.KurrentDB, Stores.MongoDB>(aggregates =>
    aggregates.Handler<PersonAggregateHandler>());
services.AddProjectionsWith<KurrentDB, RavenDB>(configuration, projections =>
    projections.Projection<PersonProjection, PersonEvents>());
```

| Member | Registers |
|---|---|
| `aggregates.Handler<THandler>()` | the Aggregate Handler, under its marker interface (its first interface that isn't `IAggregateHandler`). One without a marker interface fails when it's registered |
| `projections.Projection<TProjection, TEvents>()` | the Projection, and the Projection Handlers it declares with `IAmHandledBy`. A Projection of another events DU doesn't compile. With `[<DisabledProjection>]` it doesn't run, even when it's named |
| `….FromAssemblyOf<T>()`, `….FromAssembly(assembly)` | everything in the assembly, as when the assembly is passed: every Aggregate Handler, or every active Projection, every Projection Handler and every Custom Projection |

Whatever is both named and found in an assembly is registered once. A named Projection is created without reflection over its events DU. A named Projection of a [Custom Projection](custom-projections.md) DU also creates its Custom Projection, with the stream constants found in the Projection's assembly. These overloads are methods on `IHostApplicationBuilder`, `IHostBuilder` and `IServiceCollection` (in F# too, with method syntax). Inside the function, `aggregates.HandlerTypes` and `projections.ProjectionTypes`/`HandlerTypes` list what will be registered.

## What registration adds

| Call | Registers |
|---|---|
| `UseAggregatesWith…` | one `KurrentDBClient` (when KurrentDB is used), the RavenDB `IDocumentStore`, MongoDB database or PostgreSQL connection pool (when used), `IEventSourcedAggregateRepository` and/or `IStateBasedAggregateRepository` (singletons, with the registered `IEventUpcaster`s), and every Aggregate Handler as a transient under its marker interface |
| `UseOutboxWith<…>` | the `IOutbox` for the store; the repositories record PublishedEvents in it |
| `UseOutboxPublisher<TPublisher>` | `TPublisher` as the singleton `IOutboxPublisher`, and the hosted service that publishes the Outbox with it |
| `UseProjectionsWith…` | one `KurrentDBClient` and `KurrentDBProjectionManagementClient`, the read model store (`INoSqlStore` and/or `ISqlStore`), its checkpoint store and per-event unit of work, every Projection Handler as a transient by its concrete type, the hosted service that runs the Projections, and the custom JavaScript projections (owned by the host's `ApplicationName`: see [Custom Projections](custom-projections.md#which-projections-an-application-owns)) |
| `AddHealthChecks().AddRosettaProjections()` | the `rosetta-projections` health check |

- Services the app registered **before** Rosetta are kept: your own `KurrentDBClient` singleton, or RavenDB
  `IDocumentStore`, is used instead of Rosetta's.
- With PostgreSQL, the Aggregates, the Outbox and the Projections of one host share Rosetta's one connection pool
  (`PostgreSqlDatabase`), from the `PostgreSQL` section.
- `IEventUpcaster`s are taken from DI: register them (as singletons) in every service that reads the events.
- Each kind (Aggregates, Outbox, Outbox publisher, Projections) can be added once per service collection; a second
  call fails.
- The three-store form (`UseProjectionsWith<KurrentDB, RavenDB|MongoDB, PostgreSQL>`) exists as methods only: on
  `IHostApplicationBuilder` (with an assembly or a registration function), on `IHostBuilder` (with a registration
  function) and `AddProjectionsWith` on `IServiceCollection`. There's no curried F# function for it.
- `ProjectionRetries` (the backoff and the Faulted threshold) is registered with `TryAddSingleton`: register your own
  `ProjectionRetries` first to change it, e.g. `{ ProjectionRetries.Default with FailuresBeforeFaulted = 10 }`.
- A plain `IServiceCollection` must also provide logging, `IHostApplicationLifetime` and `IConfiguration` for the
  Projections (a generic host does).
- Both namespaces (`SI.Rosetta.Aggregates.HostBuilder`, `SI.Rosetta.Projections.HostBuilder`) define store marker
  types. In a host that registers both, qualify them, or open one namespace per file.
