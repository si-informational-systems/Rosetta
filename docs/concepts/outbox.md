---
title: Outbox
description: Recording every stored Aggregate's PublishedEvents for publishing to the outside world, without losing any.
---

# Outbox

The Outbox records the **PublishedEvents** of every Aggregate that's stored, so that another process can publish them
to the outside world (a message broker, other services) without losing any. Register it next to the Aggregates:

=== "F#"

    ```fsharp
    Host.CreateDefaultBuilder(args)
    |> fun builder -> UseAggregatesWith<KurrentDB, RavenDB> builder typeof<PersonAggregateHandler>.Assembly
    |> fun builder -> UseOutboxWith<RavenDB> builder
    |> fun builder -> builder.Build().Run()
    ```

=== "C#"

    ```csharp
    builder.UseAggregatesWith<Stores.KurrentDB, Stores.RavenDB>(typeof(PersonAggregateHandler).Assembly);
    builder.UseOutboxWith<Stores.RavenDB>();
    ```

The Outbox store is `RavenDB` or `MongoDB`, configured by its section (see [Configuration](../reference/configuration.md)).

## Settings

From the optional `Outbox` configuration section:

```json
{
  "Outbox": {
    "CollectionName": "Outbox",
    "EventTTLMinutes": 10080,
    "ExpirationFrequencyMinutes": 2160
  }
}
```

| Setting | Unless set | |
|---|---|---|
| `CollectionName` | `Outbox` | the collection the events are recorded in |
| `EventTTLMinutes` | events are kept until deleted | how long an event is kept, in minutes |
| `ExpirationFrequencyMinutes` | `2160` (36 hours) | **RavenDB only**: how often RavenDB deletes the expired events when the Outbox turns Document Expiration on (see [Expiry](#expiry)). MongoDB's TTL monitor runs every 60 seconds by itself |

Or in code, after the configuration is read:

=== "F#"

    ```fsharp
    builder.UseOutboxWith<RavenDB>(fun outbox ->
        outbox.EventTTL <- Nullable(TimeSpan.FromDays 7.0)
        outbox.ExpirationFrequency <- TimeSpan.FromHours 1.0) |> ignore
    ```

=== "C#"

    ```csharp
    builder.UseOutboxWith<Stores.RavenDB>(outbox =>
    {
        outbox.EventTTL = TimeSpan.FromDays(7);
        outbox.ExpirationFrequency = TimeSpan.FromHours(1);
    });
    ```

## When the events are recorded

Every command that completes records the PublishedEvents of its Aggregate. When the command changed the Aggregate's
`Version`, they're recorded as the Aggregate is stored, and whether the two writes are one transaction depends on where
the Aggregate lives:

| Aggregate | Outbox | The events are recorded |
|---|---|---|
| state-based, in RavenDB | `RavenDB` | with the state, in one `SaveChanges`: both are saved, or neither |
| state-based, in MongoDB | `MongoDB` | with the state, in one transaction: both are saved, or neither. MongoDB transactions need a replica set (a single node is enough) |
| event-sourced, in KurrentDB, or state-based in the other store | `RavenDB` or `MongoDB` | right after the Aggregate is stored. The two stores can't share a transaction, so a crash in between leaves the Aggregate stored without its events in the Outbox, until the command is repeated |

A command that changed nothing, e.g. an idempotent repeat, stores nothing, but the events it published are recorded on
their own. So **publish the events in the idempotent branch too**: when a crash stored an Aggregate without recording
its events, the redelivered command is idempotent, and records them this time.

```fsharp
| PersonCommands.Register cmd ->
    if this.State.Version > 0 then
        if this.State.Name <> cmd.Name then
            raise (DomainException.Named("PersonAlreadyRegistered", "Person already registered"))
        // Idempotent: nothing changes, but the event is published again
        this.PublishedEvents.Add(PersonEvents.Registered { Id = cmd.Id; Name = cmd.Name })
    else this.Register cmd
```

A rejected command (`DomainException`) or a failed store (`ConcurrencyException`) records nothing.

## One record per event

The Outbox keeps **one record per event**. An event's `Id` comes from what makes it that event: the Aggregate, its
`Version`, the event's position among the command's PublishedEvents, its type and its data. A repeat publishes the same
events at the same `Version`, so they get the same `Id`s, and the Outbox only adds the ones it doesn't hold: the record
that's there stays exactly as it was, so a RavenDB subscription or a MongoDB change stream doesn't see it again.

- RavenDB checks which events it holds, and saves the others on condition they're still new: if a concurrent repeat
  recorded one meanwhile, the save fails with RavenDB's own `ConcurrencyException` (`Raven.Client.Exceptions`; the
  command can be retried) instead of overwriting it.
- MongoDB upserts with `$setOnInsert`.

An event published again after the Aggregate changed further has a later `Version`, so it's recorded again. Whatever
consumes the events must still handle duplicates (as with any broker's redeliveries); use the `Id` as the message id.

## The recorded events

Each PublishedEvent is one document (`RavenDBOutboxEvent`, `MongoDBOutboxEvent`):

| Field | |
|---|---|
| `Id` | a UUID v8 from the SHA-256 of the Aggregate, its `Version`, the event's `Position`, `EventType` and `Data`: the same event published again has the same `Id`. In RavenDB, the document id is `{CollectionName}/{Id}` |
| `Aggregate`, `AggregateId` | the Aggregate that published it, e.g. `Person` and `People-1` |
| `Version` | the Aggregate's `Version` after the command |
| `Position` | the event's place among the PublishedEvents of its command, from 0 |
| `EventType`, `Data` | the event as KurrentDB stores it: the name its record is stored under, and the record as JSON, exactly as written |
| `RecordedAt` | when it was first recorded, in UTC. In `RecordedAt` and `Position` order, the events read back in the order they were recorded |
| `TraceParent`, `TraceState` | the command's W3C trace context, so publishing the event can continue the command's trace |

## Publishing the events

Rosetta records the events; publishing them is up to you, e.g. with a RavenDB data subscription or a MongoDB change
stream on the collection, which sends each to your broker and then deletes it (or lets it expire).
`EventEncoding.Decode` reads an event back into its DU, through the upcasters:

```fsharp
match EventEncoding.Decode<PersonEvents> upcasters recorded.EventType recorded.Data with
| Ok event -> ()            // publish it
| Error unknownEvent -> ()  // a record type PersonEvents has no case for
```

## Expiry

With `EventTTLMinutes`, each event expires that long after it was recorded. The expiry is stored in each event, so a
changed TTL applies to the events recorded from then on.

- **RavenDB** keeps it in the document's `@expires` metadata, which only Document Expiration acts on. The Outbox turns
  Document Expiration on for the database (once, on first use) when it's off, deleting the expired documents every
  `ExpirationFrequencyMinutes`: 36 hours unless set, the shortest frequency a RavenDB Community licence allows, so it
  works with every licence. An expired event can therefore stay up to that long after its TTL. The Outbox leaves an
  existing Document Expiration configuration alone. In two cases it can't turn Document Expiration on, so turn it on
  yourself (Studio: Settings → Document Expiration):
    - a certificate that can't read the database record can't check it;
    - a licence that doesn't allow the frequency refuses it (`LicenseLimitException`). The Outbox logs a Warning once,
      and the commands go on: the events are recorded, they just aren't deleted until Document Expiration is on.
- **MongoDB** keeps it in an `expiresAt` field, which a TTL index the Outbox creates on first use acts on. MongoDB
  deletes expired documents every 60 seconds.

## A custom Outbox store

Register your own singleton `IOutbox` instead of calling `UseOutboxWith`, and every command records its Aggregate's
PublishedEvents in it. See [Extending Rosetta](../contributing.md#extending-rosetta).
