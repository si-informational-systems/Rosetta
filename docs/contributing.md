---
title: Contributing
description: Building Rosetta from source, its repository layout, its tests, extending it with other stores, and these docs.
---

# Contributing

PRs, issues and suggestions are welcome at
[si-informational-systems/Rosetta](https://github.com/si-informational-systems/Rosetta).

## Building and testing

```shell
dotnet build SI.Rosetta.sln
dotnet test SI.Rosetta.sln                                  # unit + integration tests (Docker)
dotnet test --project SI.Rosetta.Projections.UnitTests      # one project
```

- `global.json` pins the .NET 11 SDK and opts `dotnet test` into the Microsoft Testing Platform.
- Every library project produces its NuGet package on build (`bin/{Configuration}`); the shared version is in
  `Directory.Build.targets`. `Directory.Build.props` makes an incomplete pattern match (FS0025) an error, and sets
  FSharp.Core once: don't add FSharp.Core references to projects.
- `SI.Rosetta.IntegrationTests` starts KurrentDB, RavenDB, MongoDB (a replica set and a standalone server) and
  PostgreSQL with Testcontainers, so Docker must run. The unit test projects need nothing.
- CI (`.github/workflows/ci.yml`) builds and tests on every push to `master` and on pull requests.

## Repository layout

| Project | |
|---|---|
| `SI.Rosetta.Common`, `.Serialization`, `.Aggregates`, `.Projections` | the core |
| `SI.Rosetta.Persistence.*` | client factories and configuration per database |
| `SI.Rosetta.Aggregates.{KurrentDB,RavenDB,MongoDB,PostgreSQL}`, `SI.Rosetta.Projections.{KurrentDB,RavenDB,MongoDB,PostgreSQL}` | the store implementations |
| `SI.Rosetta.*.HostBuilder` | registration |
| `SI.Rosetta.*.TestKit`, `SI.Rosetta.TestKit.*` | the TestKits |
| `SI.Rosetta.Aggregates.Template` | a Visual Studio item template (not built with the solution) |
| `*.UnitTests`, `SI.Rosetta.IntegrationTests` | the framework's tests |
| `Test.PL`, `Test.Aggregates`, `Test.Projections`, `Test.DTOs` | runnable samples |

### Code conventions

- F# everywhere (`Test.DTOs` is a C# stub). PascalCase for everything, including `let`-bound values.
- `task { }` everywhere; library code adds `.ConfigureAwait(false)` to every `let!`/`do!`.
- Domain language in names; commands imperative, events past tense; the `ApplyEvent` parameter is `ev`.
- Tests on xUnit v3 with NSubstitute.

## Running the samples

```shell
docker compose up -d                       # KurrentDB on :2113, RavenDB on :8080
dotnet run --project Test.Projections      # keeps running
dotnet run --project Test.Aggregates       # a test page on http://localhost:5000
```

Both read a gitignored `config/appsettings.json` from their output folder:

```json
{
  "RavenDB": { "Urls": "http://127.0.0.1:8080/", "DatabaseName": "TEST.ROSETTA" },
  "KurrentDB": { "ConnectionString": "kurrentdb://localhost:2113?tls=false" },
  "Outbox": { "CollectionName": "Outbox", "EventTTLMinutes": 10080 }
}
```

Values can be overridden on the command line: `-- --RavenDB:DatabaseName=Other`. The test page sends the Person
(event-sourced) and Organization (state-based) commands, shows each outcome (Applied, NoChanges, Rejected, Conflict)
with versions, published events and state, and lists the read models and the Outbox.

## Extending Rosetta

Every store sits behind a small interface; another event store or database plugs in by implementing them.

```fsharp
// Aggregates: store the AggregateState (state-based) or append its Changes (event-sourced)
type IAggregateRepository =
    abstract StoreAsync<'TState when 'TState :> IAggregateState> : aggregate: IAggregate<'TState> -> Task
    // An event-sourced store reads the events as state.EventsType and applies them with state.Mutate
    abstract GetAsync<'TState when 'TState : (new: unit -> 'TState) and 'TState :> IAggregateState> :
        id: string * [<Optional; DefaultParameterValue(Int64.MaxValue)>] version: int64 -> Task<Option<'TState>>

type IEventSourcedAggregateRepository = inherit IAggregateRepository
type IStateBasedAggregateRepository = inherit IAggregateRepository

// The Outbox: register a singleton IOutbox instead of calling UseOutboxWith
type IOutbox =
    abstract RecordAsync: events: OutboxEvent[] -> Task<int>   // records the ones it doesn't hold (by Id); returns how many

// ...and, for UseOutboxPublisher to publish it, IOutboxRelayStore on the same object
type IOutboxRelayStore =
    abstract TryLeaseAsync: owner: string * duration: TimeSpan * cancellationToken: CancellationToken -> Task<bool>
    abstract UnpublishedAsync: max: int * cancellationToken: CancellationToken -> Task<OutboxEvent[]>   // oldest first
    abstract MarkPublishedAsync: events: OutboxEvent[] * publishedAt: DateTime * cancellationToken: CancellationToken -> Task

// Projections: the read models, the checkpoints, and a transaction per event. While a Projection's Handlers run,
// ProjectionTransaction.Current is the event's transaction: the store writes and reads in it
type IProjectionTransaction =
    inherit IAsyncDisposable                                    // disposed without committing: rolled back
    abstract CommitAsync: checkpoint: Checkpoint -> Task         // writes the checkpoint in it, and commits

type IProjectionsUnitOfWork =
    abstract PrepareAsync: readModels: Type[] -> Task           // once per Projection at startup, with its IProjectInto types
    abstract BeginAsync: unit -> Task<IProjectionTransaction>
    abstract ClearAsync: readModels: Type[] -> Task             // a rebuilt Projection's read models

type ICheckpointStore =
    abstract ReadAsync: id: string -> Task<Checkpoint>
    abstract StoreAsync: checkpoint: Checkpoint -> Task

type IProjectionsStore =   // INoSqlStore and ISqlStore are both this
    abstract StoreAsync<'T when 'T : not struct> : doc: 'T -> Task
    abstract StoreManyAsync<'T when 'T : not struct> : docs: 'T[] -> Task
    abstract LoadAsync<'T when 'T : not struct> : id: obj -> Task<'T>
    abstract LoadManyAsync<'T when 'T : not struct> : ids: obj[] -> Task<Dictionary<obj, 'T>>
    abstract DeleteAsync<'T when 'T : not struct> : id: obj -> Task
    abstract DeleteManyAsync<'T when 'T : not struct> : ids: obj[] -> Task
```

- A store matches `ProjectionTransaction.Current` against **its own** transaction type: inside one it writes in it,
  otherwise it writes directly.
- The HostBuilders accept only Rosetta's own store markers; a new store needs its marker and registration added to the
  HostBuilder projects.

## These docs

The site is built from `docs/` with [Zensical](https://zensical.org) (configured in `zensical.toml`) and published to
GitHub Pages by `.github/workflows/docs.yml` on every push to `master` that touches them.

```shell
pip install zensical==0.1.1
zensical serve      # http://localhost:8000, rebuilds on save
zensical build --clean --strict
```

Keep the docs true to the code: when a public member, a default or a message changes, change its page in the same PR.
