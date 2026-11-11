---
title: Using Rosetta from C#
description: Hosting Rosetta, sending commands and reading events from C#.
---

# Using Rosetta from C#

Rosetta's domain model (commands and events as discriminated unions, Aggregates, Projections) is written in F#. A C#
application can still host it, send commands, read the read models and consume the events.

## Registration

Alias the store marker types: unqualified `KurrentDB` and `MongoDB` resolve to the `KurrentDB.Client` and
`MongoDB.Driver` namespaces (CS0118).

```csharp
using SI.Rosetta.Aggregates.HostBuilder;            // extension methods
using Stores = SI.Rosetta.Aggregates.HostBuilder;   // store marker types

var builder = WebApplication.CreateBuilder(args);
builder.UseAggregatesWith<Stores.KurrentDB, Stores.RavenDB>(typeof(PersonAggregateHandler).Assembly);
builder.UseOutboxWith<Stores.RavenDB>(outbox => outbox.EventTTL = TimeSpan.FromDays(7));
```

```csharp
using SI.Rosetta.Projections.HostBuilder;
using ProjectionStores = SI.Rosetta.Projections.HostBuilder;

builder.UseProjectionsWith<ProjectionStores.KurrentDB, ProjectionStores.MongoDB>(projections =>
{
    projections.Projection<PersonProjection, PersonEvents>();
    projections.StopApplicationOnFailure();
});
builder.Services.AddHealthChecks().AddRosettaProjections();
```

To change how every Projection retries a [conflict](../concepts/projections.md#conflicts-are-retried-at-once), register
a `ConflictRetries` (`SI.Rosetta.Common`):

```csharp
builder.Services.AddSingleton(new ConflictRetries(5, TimeSpan.FromMilliseconds(100)));   // or ConflictRetries.None
```

On `IServiceCollection`: `services.AddAggregatesWith<…>(configuration, assembly)`,
`services.AddProjectionsWith<…>(configuration, assembly)`, `services.AddOutboxWith<…>(configuration)`.

## Sending commands

F# union cases are created with `New{Case}` and records with their constructor:

```csharp
using SI.Rosetta.Aggregates;   // DomainException
using SI.Rosetta.Common;       // ConcurrencyException

app.MapPost("/people", async (RegisterPerson cmd, IPersonAggregateHandler people) =>
{
    try
    {
        await people.ExecuteAsync(PersonCommands.NewRegister(cmd));
        return Results.Ok(people.GetPublishedEvents());
    }
    catch (DomainException ex)
    {
        return Results.Conflict(ex.Name);
    }
    catch (ConcurrencyException)
    {
        return Results.Conflict("Conflict");   // still losing after the handler's immediate retries
    }
});
```

## Reading read models

Resolve `INoSqlStore` (or `ISqlStore`) and load by key; a missing read model is `null`:

```csharp
app.MapGet("/people/{id}", async (string id, INoSqlStore store) =>
    await store.LoadAsync<Person>(id) is { } person ? Results.Ok(person) : Results.NotFound());
```

## Consuming events

An event's data is its record as plain JSON, and its metadata's `DiscriminatedUnionCaseType` names the record it was
stored as (see [Stored format](../reference/stored-format.md)). A C# service can read events from KurrentDB, or from the
Outbox (`EventType`, `Data`), into C# records of the same shape, without F#. F# options are written as their value (or
`null`); only DUs nested inside a record keep the F# `{"Case": …, "Fields": […]}` shape.
