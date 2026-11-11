---
title: Installation
description: Which SI.Rosetta packages each project of a solution needs.
---

# Installation

Rosetta targets **.NET 11** (`net11.0`) and is published on nuget.org as the `SI.Rosetta.*` packages, all with the
same version.

## Which project needs what

| Project | Package | Brings |
|---|---|---|
| **Published Language** (commands, events, upcasters) | `SI.Rosetta.Common` | the marker interfaces, `[<EventType>]`, `IEventUpcaster`, `EventEncoding`. No database clients |
| **Domain** (states, Aggregates, Aggregate Handlers) | `SI.Rosetta.Aggregates` | the Aggregate base classes, repositories' interfaces, `DomainException` |
| **Projections** (Projections, Handlers, read models) | `SI.Rosetta.Projections` | `Projection<_>`, the attributes, `INoSqlStore`/`ISqlStore` |
| **Host** of the Aggregates (API, message consumer) | `SI.Rosetta.Aggregates.HostBuilder` | registration, plus every repository and Outbox implementation (KurrentDB, RavenDB, MongoDB, PostgreSQL) |
| **Host** of the Projections | `SI.Rosetta.Projections.HostBuilder` | registration, the subscriptions, every read model store (RavenDB, MongoDB, PostgreSQL), the health check |
| **Domain tests** | `SI.Rosetta.Aggregates.TestKit` | `TestKitBase<'THandler>` (and the Upcasting TestKit) |
| **Projection tests** | `SI.Rosetta.Projections.TestKit` | `TestKitBase<'TProjection, 'THandler, 'TEvents>` (and the Upcasting TestKit) |

```shell
dotnet add package SI.Rosetta.Common                  # Published Language
dotnet add package SI.Rosetta.Aggregates              # domain
dotnet add package SI.Rosetta.Projections             # projections
dotnet add package SI.Rosetta.Aggregates.HostBuilder  # the host that runs the Aggregates
dotnet add package SI.Rosetta.Projections.HostBuilder # the host that runs the Projections
dotnet add package SI.Rosetta.Aggregates.TestKit      # test projects
dotnet add package SI.Rosetta.Projections.TestKit
```

A small service can put everything in one project with the HostBuilder packages, which bring the others. Splitting
keeps the Published Language free of infrastructure, so other services can reference it. See
[Project structure](project-structure.md).

All packages are listed in [Packages](../reference/packages.md).

## Requirements

- **.NET 11 SDK.** Pin it in `global.json`.
- **FSharp.Core** comes with the SDK; don't add your own `PackageReference` to it.
- **Test projects** use **xUnit v3** and the Microsoft Testing Platform. See [Testing](../concepts/testing.md).
- **Infrastructure**, depending on what you use:
    - KurrentDB 26.2 (24.10 still works) with projections enabled, for Event-Sourced Aggregates, Projections and
      Custom Projections;
    - RavenDB 7.2;
    - MongoDB 8, as a **replica set** (a single node is enough) for Projections and the transactional Outbox;
    - PostgreSQL 18 (older versions with `on conflict` work) for SQL read models.

    [Operations](../guides/operations.md) has a `docker-compose.yml` for local development.

## Next

[Configuration](configuration.md), then [Your first service](first-service.md).
