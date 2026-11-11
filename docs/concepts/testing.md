---
title: Testing
description: Given/When/Then TestKits for Aggregates, Projections and upcasters, all in memory.
---

# Testing

The TestKits are built for **xUnit v3**. A test project that uses them references `xunit.v3` and is an executable:

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="xunit.v3" Version="4.0.3" />
  <PackageReference Include="SI.Rosetta.Aggregates.TestKit" Version="..." />
</ItemGroup>
```

xUnit v3 runs on the Microsoft Testing Platform. With the .NET 11 SDK, `dotnet test` only runs it after you opt in from `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

## Aggregates

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

## Projections

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

- Override `ConfigureServices` to register additional dependencies, or the other Handlers of a Projection with several `IAmHandledBy<…>`.
- The Test Kit's in-memory store is both `INoSqlStore` and `ISqlStore`, so [PostgreSQL](postgresql.md) Handlers are tested the same way, without a database.
- It commits each event as the real stores do, and delivers the Given events one at a time, in order.

## Upcasters

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