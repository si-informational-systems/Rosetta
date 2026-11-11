---
title: Your first service
description: An end-to-end walk-through, from the Published Language to a read model, with tests.
---

# Your first service

This walk-through builds a **Person** Aggregate (Event-Sourced in KurrentDB), a Projection that keeps a `Person`
document in RavenDB, an API that sends commands, and the tests for both. It takes four projects:

```text
People.PL            Published Language: commands and events        SI.Rosetta.Common
People.Domain        state, Aggregate, Aggregate Handler            SI.Rosetta.Aggregates
People.Projections   read models, Projections, Handlers             SI.Rosetta.Projections
People.Api           ASP.NET Core host for both                     SI.Rosetta.Aggregates.HostBuilder, SI.Rosetta.Projections.HostBuilder
People.Tests         TestKit tests                                  SI.Rosetta.Aggregates.TestKit, SI.Rosetta.Projections.TestKit
```

## 1. Infrastructure

```yaml title="docker-compose.yml"
services:
  kurrentdb:
    image: kurrentplatform/kurrentdb:26.2
    ports: ["2113:2113"]
    environment:
      KURRENTDB_INSECURE: "true"
      KURRENTDB_RUN_PROJECTIONS: "All"
      KURRENTDB_START_STANDARD_PROJECTIONS: "true"
      KURRENTDB_TELEMETRY_OPTOUT: "true"
  ravendb:
    image: ravendb/ravendb:7.2-latest
    ports: ["8080:8080"]
    environment:
      RAVEN_Setup_Mode: "None"
      RAVEN_License_Eula_Accepted: "true"
      RAVEN_Security_UnsecuredAccessAllowed: "PublicNetwork"
```

`KURRENTDB_START_STANDARD_PROJECTIONS` turns on `$by_category`, which builds the `$ce-People` stream the Projection reads.

## 2. Published Language

```fsharp title="People.PL/PersonMessages.fs"
namespace People.PL

open SI.Rosetta.Common

type RegisterPerson = { Id: string; Name: string }
type ChangePersonName = { Id: string; Name: string }

[<EventType("PersonRegistered")>]
type PersonRegistered = { Id: string; Name: string }

[<EventType("PersonNameChanged")>]
type PersonNameChanged = { Id: string; OldName: string; NewName: string }

[<RequireQualifiedAccess>]
type PersonCommands =
    | Register of RegisterPerson
    | ChangeName of ChangePersonName
    interface IAggregateCommands

[<RequireQualifiedAccess>]
type PersonEvents =
    | Registered of PersonRegistered
    | NameChanged of PersonNameChanged
    interface IAggregateEvents
```

## 3. Domain

```fsharp title="People.Domain/Person.fs"
namespace People.Domain

open SI.Rosetta.Aggregates
open People.PL

type PersonAggregateState() =
    inherit AggregateState<PersonEvents>()
    member val Name = "" with get, set

    override this.ApplyEvent(ev) =
        match ev with
        | PersonEvents.Registered ev ->
            this.Id <- ev.Id
            this.Name <- ev.Name
        | PersonEvents.NameChanged ev -> this.Name <- ev.NewName

type PersonAggregate() =
    inherit Aggregate<PersonAggregateState, PersonCommands>()

    override this.Execute(command) =
        match command with
        | PersonCommands.Register cmd ->
            if this.State.Version = 0L then
                this.Apply(PersonEvents.Registered { Id = cmd.Id; Name = cmd.Name })
            elif this.State.Name <> cmd.Name then
                raise (DomainException.Named("PersonAlreadyRegistered", $"{cmd.Id} is registered as {this.State.Name}"))
        | PersonCommands.ChangeName cmd ->
            if this.State.Name <> cmd.Name then
                this.Apply(PersonEvents.NameChanged { Id = cmd.Id; OldName = this.State.Name; NewName = cmd.Name })

type IPersonAggregateHandler =
    inherit IAggregateHandler

type PersonAggregateHandler(repository: IEventSourcedAggregateRepository) =
    inherit AggregateHandler<PersonAggregate, PersonCommands>()
    do base.AggregateRepository <- repository
    interface IPersonAggregateHandler

    override this.ExecuteAsync(command) =
        task {
            match command with
            | PersonCommands.Register cmd -> do! this.IdempotentlyCreateAggregate cmd.Id command
            | PersonCommands.ChangeName cmd -> do! this.IdempotentlyUpdateAggregate cmd.Id command
        }
```

## 4. Projection

```fsharp title="People.Projections/PersonProjection.fs"
namespace People.Projections

open SI.Rosetta.Projections
open People.PL

[<AutoOpen>]
module ProjectionStreamNames =
    [<Literal>]
    let PersonStream = "$ce-People"

[<CLIMutable>]
type Person = { Id: string; Name: string }

[<HandlesStream(PersonStream)>]
type PersonProjection() =
    inherit Projection<PersonEvents>()
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents>
    interface IProjectInto<Person>

and PersonProjectionHandler(store: INoSqlStore) =
    interface IProjectionHandler<PersonEvents> with
        member this.Handle(event, checkpoint) =
            task {
                match event with
                | PersonEvents.Registered e ->
                    do! store.StoreAsync({ Id = e.Id; Name = e.Name } : Person)
                | PersonEvents.NameChanged e ->
                    let! person = store.LoadAsync<Person>(e.Id)
                    if not (isNull (box person)) then
                        do! store.StoreAsync({ person with Name = e.NewName })
            }
```

## 5. Host

```fsharp title="People.Api/Program.fs"
open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open SI.Rosetta.Aggregates
open SI.Rosetta.Projections
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Projections.HostBuilder
open People.PL
open People.Domain
open People.Projections

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    builder.UseAggregatesWith<SI.Rosetta.Aggregates.HostBuilder.KurrentDB, SI.Rosetta.Aggregates.HostBuilder.RavenDB>(
        typeof<PersonAggregateHandler>.Assembly) |> ignore
    builder.UseProjectionsWith<SI.Rosetta.Projections.HostBuilder.KurrentDB, SI.Rosetta.Projections.HostBuilder.RavenDB>(
        typeof<PersonProjection>.Assembly) |> ignore
    builder.Services.AddHealthChecks().AddRosettaProjections() |> ignore

    let app = builder.Build()

    app.MapPost("/people/register", Func<RegisterPerson, IPersonAggregateHandler, _>(fun cmd people ->
        task {
            try
                do! people.ExecuteAsync(PersonCommands.Register cmd)
                return Results.Ok()
            with :? DomainException as ex ->
                return Results.Conflict(ex.Name)
        })) |> ignore

    app.MapGet("/people/{id}", Func<string, INoSqlStore, _>(fun id store ->
        task {
            let! person = store.LoadAsync<Person>(id)
            return if isNull (box person) then Results.NotFound() else Results.Ok person
        })) |> ignore

    app.MapHealthChecks("/health") |> ignore
    app.Run()
    Environment.ExitCode
```

!!! note "Store marker types"
    `SI.Rosetta.Aggregates.HostBuilder` and `SI.Rosetta.Projections.HostBuilder` both define `KurrentDB`, `RavenDB`
    and `MongoDB`. A host that registers both qualifies them, as above. A host with only one of them can
    `open SI.Rosetta.Aggregates.HostBuilder` and write `builder.UseAggregatesWith<KurrentDB, RavenDB>(…)`.

```json title="People.Api/appsettings.json"
{
  "KurrentDB": { "ConnectionString": "kurrentdb://localhost:2113?tls=false" },
  "RavenDB": { "Urls": "http://localhost:8080", "DatabaseName": "People" }
}
```

`POST /people/register {"Id": "People-1", "Name": "Ada"}` appends `PersonRegistered` to the stream `People-1`; KurrentDB
links it into `$ce-People`; the Projection stores the `Person` document; `GET /people/People-1` reads it.

## 6. Tests

```fsharp title="People.Tests/PersonTests.fs"
module People.Tests.PersonTests

open Xunit
open SI.Rosetta.Aggregates.TestKit
open SI.Rosetta.Projections.TestKit
open People.PL
open People.Domain
open People.Projections

type PersonAggregateTests() =
    inherit SI.Rosetta.Aggregates.TestKit.TestKitBase<PersonAggregateHandler>()

    [<Fact>]
    member this.``Renaming a Person changes the name``() =
        task {
            this.Given(PersonEvents.Registered { Id = "People-1"; Name = "Ada" })
            this.When(PersonCommands.ChangeName { Id = "People-1"; Name = "Ada L." })
            do! this.Then(PersonEvents.NameChanged { Id = "People-1"; OldName = "Ada"; NewName = "Ada L." })
        }

    [<Fact>]
    member this.``Registering twice under another name is rejected``() =
        task {
            this.Given(PersonEvents.Registered { Id = "People-1"; Name = "Ada" })
            this.When(PersonCommands.Register { Id = "People-1"; Name = "Grace" })
            do! this.ThenError("PersonAlreadyRegistered")
        }

type PersonProjectionTests() =
    inherit SI.Rosetta.Projections.TestKit.TestKitBase<PersonProjection, PersonProjectionHandler, PersonEvents>()

    [<Fact>]
    member this.``The document has the latest name``() =
        task {
            do! this.Given(
                    PersonEvents.Registered { Id = "People-1"; Name = "Ada" },
                    PersonEvents.NameChanged { Id = "People-1"; OldName = "Ada"; NewName = "Ada L." })
            do! this.Then({ Id = "People-1"; Name = "Ada L." } : Person)
        }
```

The test project is an xUnit v3 executable; see [Testing](../concepts/testing.md) for its `.fsproj` and `global.json`.

## Next

- Split the Aggregates and the Projections into separate hosts when they scale differently: each host registers only
  its half.
- Add the [Outbox](../concepts/outbox.md) to publish integration events.
- Read the [Guidelines](../guides/guidelines.md) before the domain grows.
