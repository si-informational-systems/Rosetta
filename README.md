# 🚀 Rosetta Framework

**Domain-Driven Design, Event Sourcing, and Projections for .NET & F# — with KurrentDB, RavenDB, MongoDB, and modern HostBuilder integration.**

[![CI](https://github.com/si-informational-systems/Rosetta/actions/workflows/ci.yml/badge.svg)](https://github.com/si-informational-systems/Rosetta/actions/workflows/ci.yml)
![.NET](https://img.shields.io/badge/.NET-10-blueviolet)
![F#](https://img.shields.io/badge/F%23-functional-blue)
![Event Sourcing](https://img.shields.io/badge/Event%20Sourcing-Enabled-brightgreen)
![RavenDB](https://img.shields.io/badge/RavenDB-Integrated-red)
![MongoDB](https://img.shields.io/badge/MongoDB-Integrated-red)
![KurrentDB](https://img.shields.io/badge/KurrentDB-Integrated-orange)

---

## ✨ Overview

**Rosetta** is a powerful, opinionated framework for building robust, Event-Sourced, Domain-Driven Microservices in .NET.

Leverage the power of **F# Discriminated Unions** inside Rosetta to create error-proof Domain Solutions!

It provides:

- 🧩 **Aggregate Roots** and **Domain Events** (DDD)
- ⚡ **Event Sourcing** with KurrentDB
- 🗄️ **State-based Aggregates** (no Event Sourcing) persisted in RavenDB or MongoDB — or mix both styles in one service
- 📊 **Projections** from KurrentDB with persistence using RavenDB and MongoDB (NoSQL)
- 📊 **Custom (Cross-Aggregate) Projection Definitions**
- 🔁 **Event Upcasting** to keep reading events written by older versions of the domain
- 🏗️ **Host Extensions** for seamless DI and service registration: `IHostBuilder`, `IHostApplicationBuilder` (`Host.CreateApplicationBuilder`, `WebApplication.CreateBuilder`) or a plain `IServiceCollection`
- 🛠️ **CQRS**-friendly patterns and handler abstractions
- 🧪 **TestKits** for BDD Testing of Aggregates, Projections and upcasters

---

## 📝 Note:

 - While **Rosetta** is opinionated in its choice of technologies, it is built to be easily extensible.
 - You can integrate any Event Sourcing Store and SQL/NoSQL database implementation with minimal effort, adapting the framework to your unique requirements and infrastructure.
 - Any additional implementations are very welcome and would contribute to the Framework's out-of-the-box usability!

```fsharp
//Implementing the db for Aggregates (either to save the entire AggregateState all at once or to save the produced Events for Event Sourcing purposes)
type IAggregateRepository =
    abstract member StoreAsync<'TAggregateState
            when 'TAggregateState :> IAggregateState> :
            aggregate: IAggregate<'TAggregateState> -> Task
    // An event-sourced store reads the events as state.EventsType and applies them with state.Mutate
    abstract member GetAsync<'TAggregateState
            when 'TAggregateState : (new : unit -> 'TAggregateState)
            and 'TAggregateState :> IAggregateState> :
        id: string * [<Optional; DefaultParameterValue(Int64.MaxValue)>] version: int64 -> Task<Option<'TAggregateState>>

type IEventSourcedAggregateRepository = inherit IAggregateRepository // e.g. KurrentDB
type IStateBasedAggregateRepository = inherit IAggregateRepository   // e.g. RavenDB, MongoDB

//Implementing the db for Projections
type ICheckpointStore =
    abstract member ReadAsync: id: string -> Task<Checkpoint> 
    abstract member StoreAsync: checkpoint: Checkpoint -> Task

type IProjectionsStore =
    abstract member StoreAsync<'T when 'T : not struct> : doc: 'T -> Task
    abstract member StoreInUnitOfWorkAsync<'T when 'T : not struct> : docs: 'T[] -> Task
    abstract member LoadAsync<'T when 'T : not struct> : id: obj -> Task<'T>
    abstract member LoadManyAsync<'T when 'T : not struct> : ids: obj[] -> Task<Dictionary<obj, 'T>>
    abstract member DeleteAsync<'T when 'T : not struct>: id: obj -> Task
    abstract member DeleteInUnitOfWorkAsync<'T when 'T : not struct>: ids: obj[] -> Task
```

## 📦 Features

- **Aggregate & Event Abstractions**: Write expressive, testable domain logic.
- **KurrentDB Integration**: Reliable, scalable event storage with optimistic concurrency.
- **State-based Aggregates**: Persist the AggregateState itself in RavenDB or MongoDB when Event Sourcing isn't needed.
- **RavenDB/MongoDB Projections**: Fast, flexible read models.
- **Event Evolution**: Upcasters rename events and fields as they're read; an event a DU doesn't know stops a Projection with a clear error, or is skipped with `[<SkipUnknownEvents>]`.
- **Plug-and-Play Host Extensions**: Register everything with a single line, on any .NET host builder.
- **OpenTelemetry-ready**: Traces and metrics for commands and projections, with one trace from a request to every read model it updates.
- **CQRS Handlers**: Command and event handler patterns out-of-the-box.
- **Idempotency & Consistency**: Built-in support for safe, repeatable operations.
- **Modern F# & C# Support**: Idiomatic, clean code for both languages.

---

## 🏗️ Architecture

```mermaid
graph TD
    A[Client/API] -->|Command| B[Aggregate Handler]
    B -->|Event| C[KurrentDB]
    B -->|AggregateState| G[RavenDB / MongoDB]
    C -->|Event| D[Projection Handler/s]
    D -->|Read Model| E[RavenDB / MongoDB]
    E -->|Query| F[Client/API]
```

---

## 🚦 Quick Start

### 1. Install NuGet Packages

Rosetta targets **.NET 10**.

```shell
# Aggregates (registers the KurrentDB, RavenDB and MongoDB repositories)
dotnet add package SI.Rosetta.Aggregates.HostBuilder

# Projections (KurrentDB subscriptions into RavenDB or MongoDB)
dotnet add package SI.Rosetta.Projections.HostBuilder

# BDD Test Kits (in your test projects; each brings SI.Rosetta.TestKit.Upcasting for testing upcasters)
dotnet add package SI.Rosetta.Aggregates.TestKit
dotnet add package SI.Rosetta.Projections.TestKit
```

The HostBuilder packages bring in `SI.Rosetta.Aggregates` / `SI.Rosetta.Projections` and every persistence implementation. Your Published Language project (Commands and Events, and their upcasters) only needs `SI.Rosetta.Common`.

### 2. Configure Infrastructure

Whether you're using Aggregates, Projections, or both - the persistence underneath needs to be properly configured. Only the sections for the stores you choose are required.

```json
{
  "RavenDB": {
    "Urls": "your_raven_url;another_raven_url",
    "CertificatePath": "relative/path/to/certificate.pfx",
    "CertificatePassword": "your_password",
    "DatabaseName": "your_db_name"
  },
  "KurrentDB": {
    "ConnectionString": "kurrentdb://localhost:2113?tls=false"
  },
  "MongoDB": {
    "Host": "localhost",
    "Port": "27017",
    "Username": "your_username",
    "Password": "your_password",
    "AuthSource": "your_auth_source",
    "DatabaseName": "your_db_name"
  }
}
```

`CertificatePath`/`CertificatePassword` and the MongoDB credentials are optional. Rosetta escapes the MongoDB username and password, so they may contain `@`, `:`, `/`, `?`, `#` or `%`.

For anything else MongoDB supports (TLS, `mongodb+srv://`, a replica set, several hosts), set the whole connection string instead. It replaces `Host`, `Port`, `Username`, `Password` and `AuthSource`, and when `DatabaseName` isn't set, the database comes from its path:

```json
{
  "MongoDB": {
    "ConnectionString": "mongodb+srv://user:pass@cluster0.example.mongodb.net/your_db_name?retryWrites=true"
  }
}
```

Rosetta uses `KurrentDB.Client` and is tested against KurrentDB 26.2. A single KurrentDB node is free to run in production from 26.2; clusters need a license.

A service has **one `KurrentDBClient`** (and one `KurrentDBProjectionManagementClient`), registered as singletons and shared by its Aggregates and every Projection subscription: a client multiplexes all of them over its gRPC connection. It's created on first use, so registering doesn't connect, and a service starts even while KurrentDB is unreachable: the calls that need it fail until it's back (subscriptions retry, see [Projections at runtime](#projections-at-runtime)). To configure the client yourself, register your own `KurrentDBClient` singleton before calling Rosetta, which keeps it.

### 3. Register Aggregates & Projections

The first generic argument chooses the Event-Sourced store, the second the store for state-based Aggregates (or Projections).

**F#**

```fsharp
// Aggregates service
open Microsoft.Extensions.Hosting
open SI.Rosetta.Aggregates.HostBuilder

Host.CreateDefaultBuilder(args)
|> fun builder -> UseAggregatesWith<KurrentDB, RavenDB> builder typeof<PersonAggregateHandler>.Assembly
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

> Both namespaces define the `KurrentDB`/`RavenDB`/`MongoDB` marker types. If one host registers both Aggregates and Projections, qualify them (e.g. `SI.Rosetta.Projections.HostBuilder.RavenDB`).

**C#**

```csharp
using Microsoft.Extensions.Hosting;
using SI.Rosetta.Aggregates.HostBuilder;            // extension methods
using Stores = SI.Rosetta.Aggregates.HostBuilder;   // store marker types

Host.CreateDefaultBuilder(args)
    .UseAggregatesWith<Stores.KurrentDB, Stores.MongoDB>(typeof(PersonAggregateHandler).Assembly)
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
| `UseAggregatesWithoutEventSourcing<TStateBased>` | every Aggregate persists its state | `RavenDB`, `MongoDB` |
| `UseAggregatesWith<TEventSourced, TStateBased>` | a mix of both | both of the above |
| `UseProjectionsWith<TEventRepository, TProjectionsStore>` | Projections | `KurrentDB` → `RavenDB`, `MongoDB` |

Each `Use…` has an `Add…` counterpart for `IServiceCollection`, and each can be used once per service collection. The stores are a closed set: only Rosetta's own `KurrentDB`, `RavenDB` and `MongoDB` compile as store arguments (the marker types can't be inherited). Aggregate Handlers and Projection Handlers are discovered in the given assembly and registered automatically.

---

## 🧑‍💻 Example: Person Aggregate

### Domain Messages (Published Language)

Every Discriminated Union case wraps exactly **one record**, and record type names must be unique - the record type name (e.g. `PersonRegistered`) is the event type stored in KurrentDB.

```fsharp
open SI.Rosetta.Common

type RegisterPerson = {
    Id: string
    Name: string
}

type ChangePersonName = {
    Id: string
    Name: string
}

type PersonRegistered = {
    Id: string
    Name: string
}

type PersonNameChanged = {
    Id: string
    OldName: string
    NewName: string
}

[<RequireQualifiedAccess>]
type PersonCommands =
    | Register of RegisterPerson
    | ChangeName of ChangePersonName
    interface IAggregateCommands // [REQUIRED]

[<RequireQualifiedAccess>]
type PersonEvents =
    | Registered of PersonRegistered
    | NameChanged of PersonNameChanged
    interface IAggregateEvents // [REQUIRED]
```

### State

```fsharp
type PersonAggregateState() =
    inherit AggregateState<PersonEvents>() // <IAggregateEvents>
    member val Name = "" with get, set

    override this.ApplyEvent(ev) =
        match ev with
        | PersonEvents.Registered ev -> 
            this.Id <- ev.Id
            this.Name <- ev.Name
        | PersonEvents.NameChanged ev -> 
            this.Name <- ev.NewName
```

Each applied event increments `Version`, which is used for optimistic concurrency (KurrentDB) and idempotency checks.

### Domain Model

```fsharp
type PersonAggregate() =
    inherit Aggregate<PersonAggregateState, PersonCommands>() // <AggregateState, IAggregateCommands>

    override this.Execute(command: PersonCommands) =
        match command with
        | PersonCommands.Register cmd ->
            if this.State.Version > 0 then
                if this.State.Name <> cmd.Name then
                    raise (DomainException.Named("PersonAlreadyRegistered", "Person already registered"))
            else this.Register cmd
        | PersonCommands.ChangeName cmd ->
            if this.State.Name <> cmd.Name then this.ChangeName cmd

    member private this.Register(cmd: RegisterPerson) =
        let event = PersonEvents.Registered { Id = cmd.Id; Name = cmd.Name }
        this.PublishedEvents.Add event // OPTIONAL: integration events for the outside world
        this.Apply event

    member private this.ChangeName(cmd: ChangePersonName) =
        let event = PersonEvents.NameChanged { Id = cmd.Id; OldName = this.State.Name; NewName = cmd.Name }
        this.Apply event
```

`this.Apply` only compiles with the events DU of the Aggregate's state: `PersonEvents` here, from `AggregateState<PersonEvents>`.

### Handler

```fsharp
type IPersonAggregateHandler = // [REQUIRED] the Handler is registered in DI under this interface
    inherit IAggregateHandler

// IEventSourcedAggregateRepository - Event Sourcing; IStateBasedAggregateRepository - persist the AggregateState instead
type PersonAggregateHandler(repo: IEventSourcedAggregateRepository) =
    inherit AggregateHandler<PersonAggregate, PersonCommands>() // <Aggregate, IAggregateCommands>
    do base.AggregateRepository <- repo
    interface IPersonAggregateHandler

    override this.ExecuteAsync(command) =
        task {
            match command with
            | PersonCommands.Register cmd -> do! this.IdempotentlyCreateAggregate cmd.Id command
            | PersonCommands.ChangeName cmd -> do! this.IdempotentlyUpdateAggregate cmd.Id command
        }
```

- `IdempotentlyUpdateAggregate` raises `DomainException.Named("PersonDoesNotExist")` when the Aggregate doesn't exist.
- The Aggregate is stored only when its `Version` changed.
- With KurrentDB the stream name is the Aggregate `Id` - use ids like `People-1` so that the `$ce-People` category stream can feed Projections.

### Usage

```fsharp
type PersonConsumer(handler: IPersonAggregateHandler) =
    member this.Consume(cmd: RegisterPerson) =
        task {
            do! handler.ExecuteAsync(PersonCommands.Register cmd)
            let integrationEvents = handler.GetPublishedEvents()
            ()
        }
```

### State-based persistence

| Store | Where the state lives | Where `Version` lives |
|---|---|---|
| RavenDB | the AggregateState document | the document's `@metadata` (`Rosetta-Aggregate-Version`) |
| MongoDB | the AggregateState document, in the pluralized state type name collection | a regular `version` field |

---

## 📊 Example: Person Projection

```fsharp
[<AutoOpen>]
module ProjectionStreamNames =
    [<Literal>]
    let PersonStream = "$ce-People"

[<CLIMutable>]
type Person = {
    Id: string
    Name: string
}

[<HandlesStream(ProjectionStreamNames.PersonStream)>]
type PersonProjection() =
    inherit Projection<PersonEvents>() // <IAggregateEvents>
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents> // <IProjectionHandler, IAggregateEvents>

and PersonProjectionHandler(store: INoSqlStore) =
    interface IProjectionHandler<PersonEvents> with
        member this.Handle(event, checkpoint) =
            task {
                match event with
                | PersonEvents.Registered e ->
                    do! store.StoreAsync({ Id = e.Id; Name = e.Name } : Person)
                | PersonEvents.NameChanged e ->
                    do! this.Project(e.Id, fun person -> { person with Name = e.NewName })
            }

    member private this.Project(id: string, update: Person -> Person) =
        task {
            let! person = store.LoadAsync<Person>(id)
            do! store.StoreAsync(update person)
        }
```

- A Projection can be handled by several Handlers - add one `IAmHandledBy<…>` per Handler.
- `IAmHandledBy<Handler, Events>` must name the Projection's own events DU: a Handler of another DU doesn't compile (FS0366 "No implementation was given for 'abstract IProjectionOf.EventsType…'" on that line).
- Checkpoints are stored per Projection (`Checkpoints-{ProjectionName}`), so a restarted service continues where it stopped.
- Add `[<DisabledProjection>]` to switch a Projection off.

### Projections at runtime

- Projections run in a `BackgroundService`. The application finishes starting right away, and stopping it (Ctrl+C) cancels every subscription immediately.
- A Projection's Handlers handle each event in parallel. When any of them fails, the `ProjectionException` holds every Handler's failure, and the checkpoint isn't stored.
- When a subscription drops (KurrentDB is unavailable, or a Handler throws because, say, the read store is down), it resubscribes from its last checkpoint **up to 5 times**, waiting **1, 2, 4, 8 and 16 s**. Only progress resets the count: a handled event, or catching up with the stream. A Handler that keeps failing on the same event therefore doesn't retry forever.
- If a Projection still fails, **the whole application stops**: Rosetta logs it as Critical, sets `Environment.ExitCode` to 1 and stops the host. The same happens when the Projections can't start, e.g. when KurrentDB is down at startup.
- An event the Projection's events DU has no case for stops it **at once**, without resubscribing, since retrying can't help (see [Evolving events](#-evolving-events-upcasting-and-unknown-events)).
- A C# `void Main` exits with `Environment.ExitCode` on its own. An F# `main` returns its exit code explicitly, so return `Environment.ExitCode`, or the process exits with 0 and restart policies won't notice the failure:

```fsharp
host.Run()
Environment.ExitCode
```

---

## 📊 Example: Cross-Aggregate Custom Projections

Custom Projections combine events from several Aggregates into one stream. Rosetta generates and deploys the KurrentDB JavaScript projection for you on startup.

```fsharp
[<AutoOpen>]
module ProjectionStreamNames =
    [<Literal>]
    let PersonStream = "$ce-People"
    [<Literal>]
    let OrganizationStream = "$ce-Organizations"
    [<Literal>]
    let TotalPeopleAndOrganizationsStream = "cp-TotalPeopleAndOrganizations"

[<CLIMutable>]
type Totals = {
    Id: string
    TotalPeople: int
    TotalOrganizations: int
}

[<CustomProjectionStream(ProjectionStreamNames.TotalPeopleAndOrganizationsStream)>]
type TotalPeopleAndOrganizationsEvents = 
    | PersonRegistered of PersonRegistered
    | OrganizationRegistered of OrganizationRegistered
    interface ICustomProjectionEvents

[<HandlesStream(ProjectionStreamNames.TotalPeopleAndOrganizationsStream)>]
type TotalPeopleAndOrganizationsProjection() =
    inherit Projection<TotalPeopleAndOrganizationsEvents>()
    interface IAmHandledBy<TotalPeopleAndOrganizationsProjectionHandler, TotalPeopleAndOrganizationsEvents>

and TotalPeopleAndOrganizationsProjectionHandler(store: INoSqlStore) =
    let TotalsId = "Totals-All"

    interface IProjectionHandler<TotalPeopleAndOrganizationsEvents> with
        member this.Handle(event, checkpoint) =
            task {
                match event with
                | TotalPeopleAndOrganizationsEvents.PersonRegistered _ ->
                    do! this.Project(fun totals -> { totals with TotalPeople = totals.TotalPeople + 1 })
                | TotalPeopleAndOrganizationsEvents.OrganizationRegistered _ ->
                    do! this.Project(fun totals -> { totals with TotalOrganizations = totals.TotalOrganizations + 1 })
            }

    member private this.Project(update: Totals -> Totals) =
        task {
            let! totals = store.LoadAsync<Totals>(TotalsId)
            let current =
                if isNull (box totals) then { Id = TotalsId; TotalPeople = 0; TotalOrganizations = 0 }
                else totals
            do! store.StoreAsync(update current)
        }
```

Conventions the generator relies on:

- The Custom Projection stream name **must start with `cp-`**.
- The cases reuse the Aggregates' event records (`PersonRegistered`, `OrganizationRegistered`).
- For every source Aggregate events DU (`PersonEvents`, `OrganizationEvents`), the Projections assembly must contain a `string` literal named `{AggregateName}Stream` (`PersonStream`, `OrganizationStream`) holding its category stream.

How the JavaScript projections are deployed on startup:

- A projection is **created** when no KurrentDB projection with its name (`{StreamName without cp-}CustomProjection`) exists. It's created on the V1 projections engine: V2 is still experimental in KurrentDB, and the engine can only be chosen at creation.
- An existing projection is **updated only when needed**. Rosetta reads its stored definition over the KurrentDB HTTP API, using the address, credentials and TLS settings of `KurrentDB:ConnectionString`. It re-submits the definition when the query changed, when emit is disabled, or when the projection is Faulted (re-submitting restarts it). Otherwise the projection is left alone.
- If the stored definition can't be read, the definition is re-submitted anyway and a warning is logged.
- **Renaming** the stream creates a new projection. The old one is neither removed nor stopped, and keeps linking events into the old stream.
- A **changed definition** (e.g. a new event case) applies from the projection's last persisted checkpoint, not from the start of history. Older events of the new type are only partly linked, so reset the projection in KurrentDB, and rebuild the Read Model, if they matter.

---

## 🔁 Evolving events: upcasting and unknown events

An event is stored under the **name of its record type**, and read back into the DU case whose record has that name. Changing a record would therefore leave the events already in KurrentDB unreadable, unless you use one of these:

| The record… | Old events stay readable with |
|---|---|
| gains a field | nothing: older events read it as its default (`null`, `0`, `None`) |
| loses a field | nothing: the stored value is ignored |
| is renamed | `[<EventType("OldName")>]` on the renamed record |
| renames, retypes or splits fields, or is renamed without an `[<EventType>]` | an upcaster |

### The stored format

An event's data is its record as plain JSON (System.Text.Json), and its metadata names the record it's stored under, so the events don't depend on F#: a C# service can read them into C# records. F# options are written as their value, or `null` for `None`. DUs inside a record keep the `{"Case": …, "Fields": […]}` shape, so they're F#-only.

### Stable event type names

`[<EventType>]` fixes the name a record is stored under (and that Custom Projections link it by), so the record type can be renamed freely:

```fsharp
open SI.Rosetta.Common

[<EventType("PersonRegistered")>]   // the name its events were stored under
type PersonSignedUp = { Id: string; Name: string }
```

Keep the name once events are stored. To make every rename safe from the start, give each event record an `[<EventType>]` with its current name. Two cases of one DU can't share a name: reading that DU fails with an error naming them.

### Upcasters

An **upcaster** rewrites old events, as they're read, into the shape the current DU expects:

```fsharp
open SI.Rosetta.Common   // where IEventUpcaster lives, so a Published Language project can hold its upcasters

/// Older events were PersonCreated, with FullName for Name
type PersonCreatedUpcaster() =
    interface IEventUpcaster with
        member _.Upcast(ev) =
            match ev.TypeName with
            | "PersonCreated" -> { ev.RenameField("FullName", "Name") with TypeName = "PersonRegistered" }
            | _ -> ev

services.AddSingleton<IEventUpcaster, PersonCreatedUpcaster>()
```

`ev.Data` is the record as a System.Text.Json `JsonObject`, parsed for each event read, so an upcaster may change it in place. `RenameField` covers the common case. For other changes, use the `JsonObject` API directly: a JSON node belongs to one object, so remove it (or `DeepClone` it) before adding it under another name.

- Register them in **every service that reads the events**: the Aggregates service rebuilds Aggregates from their stored events, too. Register them as singletons: one instance serves every Projection and command at once, so keep them stateless.
- `TypeName` is the name the event is stored under (its record's `[<EventType>]` name, or the record type's name), and the upcaster returns the name of the record it should be read as.
- Every event read from KurrentDB, by Aggregates and Projections alike, goes through every registered upcaster, **in registration order**, each getting the previous one's output. So register a chain oldest step first (v1→v2 before v2→v3), or upcast every old shape of an event straight to the current one in a single upcaster, which doesn't depend on order. Only the reading side changes: the stored events stay as they were written.
- An event whose record type the DU has no case for (even after upcasting) throws an `UnknownEventException` that names the event, its stream and its number.
- An **Aggregate** always fails on such an event: rebuilding its state without it would be wrong.
- A **Projection** stops on it, unless it's marked `[<SkipUnknownEvents>]`. Then it logs the event (a warning the first time per event type) and moves past it. Use that for Projections that may see events added to the domain before they handle them:

```fsharp
[<SkipUnknownEvents>]
[<HandlesStream(ProjectionStreamNames.PersonStream)>]
type PersonProjection() =
    inherit Projection<PersonEvents>()
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents>
```

---

## 📈 Observability

Rosetta traces and measures commands and projections with .NET's own `ActivitySource` and `Meter`, both named **`SI.Rosetta`**. It records nothing, and costs next to nothing, until your application listens. To export them with OpenTelemetry (packages `OpenTelemetry.Extensions.Hosting` and an exporter, e.g. `OpenTelemetry.Exporter.OpenTelemetryProtocol`):

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("SI.Rosetta").AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddMeter("SI.Rosetta").AddOtlpExporter());
```

```fsharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(fun tracing -> tracing.AddSource("SI.Rosetta").AddOtlpExporter() |> ignore)
    .WithMetrics(fun metrics -> metrics.AddMeter("SI.Rosetta").AddOtlpExporter() |> ignore) |> ignore
```

**Traces**

| Span | Kind | Tags |
|---|---|---|
| `{Aggregate} {Command}`, e.g. `Person RegisterPerson` | Internal | `rosetta.aggregate`, `rosetta.aggregate.id`, `rosetta.command`, `rosetta.events` (stored), `rosetta.outcome` (`handled`, `rejected`, `failed`), `rosetta.domain_error` |
| `{Projection} {Event}`, e.g. `Person PersonRegistered` | Consumer | `rosetta.projection`, `rosetta.stream`, `rosetta.event`, `rosetta.checkpoint`, `rosetta.outcome` (`skipped`) |

A command's trace context (W3C `traceparent`/`tracestate`) is stored in the metadata of the events it produces, and the projections handling those events continue it. So one trace runs from the request, through the command, to every read model it updates, even across services. A rejected command (`DomainException`) or a failing projection marks its span as an error. Spans of your own code and of instrumented clients nest inside Rosetta's.

**Metrics**

| Instrument | Unit | Tags |
|---|---|---|
| `rosetta.commands` | `{command}` | `rosetta.aggregate`, `rosetta.command`, `rosetta.outcome` |
| `rosetta.command.duration` | `s` | `rosetta.aggregate`, `rosetta.command` |
| `rosetta.events.stored` | `{event}` | `rosetta.aggregate` |
| `rosetta.projection.events` | `{event}` | `rosetta.projection`, `rosetta.outcome` (`handled`, `failed`, `skipped`) |
| `rosetta.projection.event.duration` | `s` | `rosetta.projection` |
| `rosetta.projection.resubscriptions` | `{resubscription}` | `rosetta.projection` |

**Logs** still go through `ILogger`, configured by your application as before. With OpenTelemetry logging (`builder.Logging.AddOpenTelemetry(…)`), every entry written during a command or a projection also carries its trace and span ids.

---

## 🧪 Testing

The TestKits are built for **xUnit v3**. A test project that uses them references `xunit.v3` and is an executable:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="xunit.v3" Version="4.0.2" />
  <PackageReference Include="SI.Rosetta.Aggregates.TestKit" Version="..." />
</ItemGroup>
```

xUnit v3 runs on the Microsoft Testing Platform. With the .NET 10 SDK, `dotnet test` only runs it after you opt in from `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

### Aggregates

```fsharp
type PersonTests() =
    inherit TestKitBase<PersonAggregateHandler>()

    [<Fact>]
    member this.``Should change the name``() =
        task {
            this.Given(PersonEvents.Registered { Id = "People-1"; Name = "John" })
            this.When(PersonCommands.ChangeName { Id = "People-1"; Name = "Johnny" })
            do! this.Then(PersonEvents.NameChanged { Id = "People-1"; OldName = "John"; NewName = "Johnny" })
        }

    [<Fact>]
    member this.``Should fail for a missing Person``() =
        task {
            this.Given()
            this.When(PersonCommands.ChangeName { Id = "People-1"; Name = "Johnny" })
            do! this.ThenError("PersonDoesNotExist")
        }
```

- `Then(producedEvents)`, `Then(producedEvents, publishedEvents)`, `ThenNoEvents()` and `ThenError(name)` are available; a test that never calls one of them fails.
- The Handler must take a single repository constructor argument, and every command record needs an `Id: string`.
- `ThenError` matches `DomainException.Named(name, …)`.

### Projections

```fsharp
type PersonProjectionTests() =
    inherit TestKitBase<PersonProjection, PersonProjectionHandler, PersonEvents>()

    [<Fact>]
    member this.``Should project the renamed Person``() =
        task {
            do! this.Given(
                    PersonEvents.Registered { Id = "People-1"; Name = "John" },
                    PersonEvents.NameChanged { Id = "People-1"; OldName = "John"; NewName = "Johnny" })
            do! this.Then({ Id = "People-1"; Name = "Johnny" } : Person)
        }
```

Override `ConfigureServices` to register additional dependencies - or the other Handlers of a Projection with several `IAmHandledBy<…>`.

### Upcasters

The Aggregates and Projections TestKits take the current events DU only: they test the domain model as it is now. An upcaster turns an older version's events into exactly those events, so it's tested on its own, with `UpcastingTestKitBase` (`SI.Rosetta.TestKit.Upcasting`, which comes with either TestKit). `Given` events are encoded as they're stored and read back through the upcasters registered in `ConfigureServices`, as Rosetta reads events from KurrentDB. `Then` lists the events of the current DU they must be read as.

To write an event as an older version of the domain did, declare its old shape in the test project. The record type name is what's stored, so it must be the old name:

```fsharp
open SI.Rosetta.TestKit

/// PersonRegistered as the previous version wrote it
type PersonCreated = { Id: string; FullName: string }

[<RequireQualifiedAccess>]
type OldPersonEvents =
    | Created of PersonCreated
    interface IAggregateEvents

type PersonUpcastingTests() =
    inherit UpcastingTestKitBase<PersonEvents>()

    override this.ConfigureServices(services) =
        services.AddSingleton<IEventUpcaster, PersonCreatedUpcaster>() |> ignore

    [<Fact>]
    member this.``PersonCreated is read as PersonRegistered``() =
        this.Given(OldPersonEvents.Created { Id = "People-1"; FullName = "John" })
        this.Then(PersonEvents.Registered { Id = "People-1"; Name = "John" })
```

- `Given` takes events of any DU, so a test can also check that current events pass the upcasters unchanged.
- An event that still has no case after the upcasters throws `UnknownEventException`.

---

## 🛠️ Building from source

```shell
dotnet build SI.Rosetta.sln
dotnet test SI.Rosetta.sln
```

`global.json` pins the .NET 10 SDK. Every library project produces its NuGet package on build. `SI.Rosetta.Aggregates.Template` is a Visual Studio item template and is excluded from the solution build (see its own README).

`dotnet test` also runs `SI.Rosetta.IntegrationTests`, which start KurrentDB, RavenDB and MongoDB in Docker with [Testcontainers](https://dotnet.testcontainers.org/), so Docker must be running. To run only the unit tests, pass `--project SI.Rosetta.Aggregates.UnitTests` or `--project SI.Rosetta.Projections.UnitTests`. CI (`.github/workflows/ci.yml`) builds and runs every test on each push to `master` and on pull requests.

---

## 🐳 Running the samples

`docker-compose.yml` starts the infrastructure the samples need, unsecured and for local development only: KurrentDB (with projections enabled) and RavenDB.

```shell
docker compose up -d     # KurrentDB UI: http://localhost:2113, RavenDB Studio: http://localhost:8080
```

Both samples read `config/appsettings.json` from their project folder. It's gitignored, so create it in `Test.Aggregates/config/` and `Test.Projections/config/`:

```json
{
  "RavenDB": { "Urls": "http://127.0.0.1:8080/", "DatabaseName": "TEST.ROSETTA" },
  "KurrentDB": { "ConnectionString": "kurrentdb://localhost:2113?tls=false" }
}
```

```shell
dotnet run --project Test.Projections   # keeps running: projects $ce-People and the cp-TotalPeopleAndOrganizations Custom Projection
dotnet run --project Test.Aggregates    # 2nd terminal: the test page, on http://localhost:5000
```

Test.Aggregates is a small ASP.NET Core app (`--urls` changes its address). Its test page sends the Person (Event-Sourced, KurrentDB) and Organization (state-based, RavenDB) commands through their Aggregate Handlers, and logs what each command did:

- **Applied**, **NoChanges** (an idempotent repeat) or **Rejected** (a `DomainException`, by name or message);
- the Aggregate's Version before and after, the published events and the resulting state.

**Scenarios** cover every case in one click: registering, renaming and setting the height; idempotent repeats; registering an existing Id under another name; changing a Person that doesn't exist; and the same for an Organization.

The page's tables list the RavenDB documents (database `TEST.ROSETTA`), which you can also browse in RavenDB Studio. `People` and `TotalPeoples` come from the Projections, so they fill only while Test.Projections runs. `Organizations` holds the state-based Aggregate, whose Version is in the document's metadata. Organization never writes to KurrentDB, so `TotalOrganizations` stays 0. RavenDB refuses cross-origin requests, so the page reads the documents through the app (`GET /people`, `/organizations`, `/totals`).

`docker compose down` stops the containers, and `docker compose down -v` also deletes their data.

---

## ⬆️ Upgrading from EventStoreDB 24.10 to KurrentDB

KurrentDB 26.x upgrades a 24.10 database in place. Back it up first.

1. **Move the services to the Rosetta version that uses `KurrentDB.Client` first.** It works against both 24.10 and 26.2, so the services don't have to change at the same time as the server. Rosetta's own names changed with it:
   - the packages `SI.Rosetta.Persistence.EventStore`, `SI.Rosetta.Aggregates.EventStore` and `SI.Rosetta.Projections.EventStore` are now `….KurrentDB` (the HostBuilder packages bring them in);
   - the configuration section `EventStoreDB` is now `KurrentDB`. A service with only the old section fails at startup with `KurrentDB:ConnectionString is missing from the configuration`;
   - the store marker type `EventStore` is now `KurrentDB`, and the `EventStore*`/`ES*` types are now `KurrentDB*`;
   - `esdb://` connection strings still work, and `kurrentdb://` is the new scheme.
2. **Rename the server settings.** `EVENTSTORE_*` environment variables become `KURRENTDB_*`, and `eventstore.conf` becomes `kurrentdb.conf`. The data and log paths move from `/var/lib/eventstore` and `/var/log/eventstore` to `/var/lib/kurrentdb` and `/var/log/kurrentdb`. KurrentDB sends anonymous usage statistics to Kurrent unless `KURRENTDB_TELEMETRY_OPTOUT` is `true`.
3. **In Docker, give the data to the new user.** The EventStoreDB image runs as uid 1000, but the KurrentDB image (`kurrentplatform/kurrentdb`) runs as uid 1001. If the data directory isn't writable, KurrentDB doesn't fail. It logs `Access to path /var/lib/kurrentdb denied`, starts an **empty** database inside the container and still reports healthy. Fix the ownership before the first start, then check that your streams are there before the services connect:

   ```shell
   docker run --rm -v <data-volume>:/data -v <logs-volume>:/logs alpine chown -R 1001:1001 /data /logs
   ```

System projections such as `$by_category`, existing Custom Projections and the Projections' checkpoints carry over unchanged. Rosetta doesn't need AtomPub (`ENABLE_ATOM_PUB_OVER_HTTP`): it only reads projection definitions over the HTTP projections API, which is always on.

To keep the data of the samples' older `eventstore` container, copy its volume before the first `docker compose up`:

```shell
docker compose create kurrentdb
docker run --rm -v rosetta_eventstore-data:/from:ro -v rosetta_kurrentdb-data:/to alpine sh -c "cp -a /from/. /to/ && chown -R 1001:1001 /to"
docker compose up -d --remove-orphans
```

---

## 🏆 Why Rosetta?

- **DDD-first**: Designed for rich, expressive Domain Models.
- **Event Sourcing**: Every state change is an Event, not just a row update.
- **Projections**: Build fast, query-optimized Read Models.
- **Extensible**: Add your own Aggregates, (Custom) Projections, and Stores.
- **Modern .NET**: Async/await, DI, HostBuilder, and more.

---

## 🛡️ Best Practices

- Use **CQRS**: Separate command and query responsibilities.
- Keep **Domain logic** in Aggregates, not Handlers or Controllers.
- Register all services via HostBuilder extensions.
- Write minimal-code **Unit Tests** for Aggregates and Projections using **Aggregate Test Kit** and **Projections Test Kit**, and for upcasters using **Upcasting Test Kit**.
- When an event changes shape, **upcast** the stored events instead of rewriting them, and deploy the services that read new events (Projections) before the ones that write them (Aggregates).

---

## 📚 Documentation

- [Event Sourcing Concepts](https://martinfowler.com/eaaDev/EventSourcing.html)
- [Domain-Driven Design](https://dddcommunity.org/)
- [RavenDB Docs](https://ravendb.net/docs/)
- [MongoDB Docs](https://www.mongodb.com/docs/)
- [KurrentDB Docs](https://docs.kurrent.io/)

---

## 🤝 Contributing

PRs, issues, and suggestions are welcome!  
Please follow the DDD and .NET conventions outlined in this repo.

---

## 📝 License

MIT License

---

## 💬 Questions?

Open an issue or start a discussion!

---

**Happy coding!** 🚀
