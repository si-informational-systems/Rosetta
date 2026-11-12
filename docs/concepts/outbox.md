---
title: Outbox
description: Recording every stored Aggregate's PublishedEvents, and publishing them to the outside world, without losing any.
---

# Outbox

The Outbox records the **PublishedEvents** of every Aggregate that's stored, and its publisher sends them to the
outside world (a message broker, other services) without losing any. Register it next to the Aggregates:

=== "F#"

    ```fsharp
    Host.CreateDefaultBuilder(args)
    |> fun builder -> UseAggregatesWith<KurrentDB, RavenDB> builder typeof<PersonAggregateHandler>.Assembly
    |> fun builder -> UseOutboxWith<RavenDB> builder
    |> fun builder -> UseOutboxPublisher<YourEventPublisher> builder   // optional, your own IOutboxPublisher: see Publishing the events
    |> fun builder -> builder.Build().Run()
    ```

=== "C#"

    ```csharp
    builder.UseAggregatesWith<Stores.KurrentDB, Stores.RavenDB>(typeof(PersonAggregateHandler).Assembly);
    builder.UseOutboxWith<Stores.RavenDB>();
    builder.UseOutboxPublisher<YourEventPublisher>();   // optional, your own IOutboxPublisher: see Publishing the events
    ```

The Outbox store is `RavenDB`, `MongoDB` or `PostgreSQL`, configured by its section (see [Configuration](../reference/configuration.md)).

## Settings

From the optional `Outbox` configuration section:

```json
{
  "Outbox": {
    "CollectionName": "Outbox",
    "EventTTLMinutes": 10080,
    "ExpirationFrequencyMinutes": 2160,
    "PublishBatchSize": 100,
    "PublishPollIntervalMilliseconds": 1000
  }
}
```

| Setting | Unless set | |
|---|---|---|
| `CollectionName` | `Outbox` | the collection the events are recorded in; in PostgreSQL, the table `rosetta.{snake_case}` (`rosetta.outbox`) |
| `EventTTLMinutes` | events are kept until deleted | how long an event is kept, in minutes: from its recording, or with a publisher from its publication (see [Expiry](#expiry)) |
| `ExpirationFrequencyMinutes` | `2160` (36 hours) | **RavenDB and PostgreSQL**: how often RavenDB deletes the expired events when the Outbox turns Document Expiration on, or how often, at most, the PostgreSQL Outbox deletes them itself (see [Expiry](#expiry)). MongoDB's TTL monitor runs every 60 seconds by itself |
| `PublishBatchSize` | `100` | with a publisher: how many events it reads at a time |
| `PublishPollIntervalMilliseconds` | `1000` | with a publisher: how long it waits before looking again, when it found less than a full batch |

Or in code, after the configuration is read, where `PublishLeaseDuration` (30 seconds, see
[One instance at a time](#one-instance-at-a-time)) can be set too:

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

The values are checked at registration: a positive TTL, a frequency and a lease of a second or more, a batch of at
least 1 and a positive poll interval.

## When the events are recorded

Every command that completes records the PublishedEvents of its Aggregate. When the command changed the Aggregate's
`Version`, they're recorded as the Aggregate is stored, and whether the two writes are one transaction depends on where
the Aggregate lives:

| Aggregate | Outbox | The events are recorded |
|---|---|---|
| state-based, in RavenDB | `RavenDB` | with the state, in one `SaveChanges`: both are saved, or neither |
| state-based, in MongoDB | `MongoDB` | with the state, in one transaction: both are saved, or neither. MongoDB transactions need a replica set (a single node is enough) |
| state-based, in PostgreSQL | `PostgreSQL` | with the state, in one transaction: both are saved, or neither |
| event-sourced, in KurrentDB, or state-based in another store | `RavenDB`, `MongoDB` or `PostgreSQL` | right after the Aggregate is stored. The two stores can't share a transaction, so a crash in between leaves the Aggregate stored without its events in the Outbox, until the command is repeated |

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
that's there stays exactly as it was, published or not, so it isn't published again.

- RavenDB checks which events it holds, and saves the others on condition they're still new: if a concurrent repeat
  recorded one meanwhile, the save fails with `ConcurrencyException` (Rosetta's, wrapping RavenDB's; the handler runs
  the command again at once, see [Conflicts](aggregates.md#conflicts)) instead of overwriting it.
- MongoDB upserts with `$setOnInsert`.
- PostgreSQL inserts with `on conflict (id) do nothing`.

An event published again after the Aggregate changed further has a later `Version`, so it's recorded again. Whatever
consumes the events must still handle duplicates (as with any broker's redeliveries); use the `Id` as the message id.

## The recorded events

Each PublishedEvent is one document (`RavenDBOutboxEvent`, `MongoDBOutboxEvent`), or one row of the PostgreSQL table,
with the same fields as snake_case columns (see [Stored format](../reference/stored-format.md#an-event-in-the-outbox)):

| Field | |
|---|---|
| `Id` | a UUID v8 from the SHA-256 of the Aggregate, its `Version`, the event's `Position`, `EventType` and `Data`: the same event published again has the same `Id`. In RavenDB, the document id is `{CollectionName}/{Id}` |
| `Aggregate`, `AggregateId` | the Aggregate that published it, e.g. `Person` and `People-1` |
| `Version` | the Aggregate's `Version` after the command |
| `Position` | the event's place among the PublishedEvents of its command, from 0 |
| `EventsUnionClrType` | the events union the event is a case of, as `Namespace.Type, Assembly`, e.g. `MyApp.PL.PersonEvents, MyApp.PL`: what the publisher decodes it as. No version, culture or public key token, so it still resolves after the assembly's version changes. `null` in records from before 1.38 |
| `EventClrType` | the case's record type as it was when the event was recorded, e.g. `MyApp.PL.PersonRegistered, MyApp.PL`: for tools, and for reading a record without its union. `null` in records from before 1.38 |
| `EventType`, `Data` | the event as KurrentDB stores it: the name its record is stored under, and the record as JSON, exactly as written |
| `RecordedAt` | when it was first recorded, in UTC. In `RecordedAt` and `Position` order, the events read back in the order they were recorded |
| `TraceParent`, `TraceState` | the command's W3C trace context, so publishing the event can continue the command's trace |
| `PublishedAt` | when the [publisher](#publishing-the-events) published it, in UTC; `null` until then |

## Publishing the events

Implement `IOutboxPublisher`, which sends one event to your broker, and register it with `UseOutboxPublisher`: Rosetta
reads the unpublished events, decodes each one, hands it to your publisher in order, and marks it published.

```fsharp
type IOutboxPublisher =
    abstract PublishAsync: publication: OutboxPublication * cancellationToken: CancellationToken -> Task
```

`OutboxPublication` is the event exactly as the Aggregate published it, and everything known about it. Rosetta builds
it for each call (it's never stored), decoding the event through the registered [upcasters](event-evolution.md), so your app
keeps no mapping from Aggregates to events unions:

| Field | |
|---|---|
| `Event` | the event as the Aggregate published it: a case of its events union (`PersonEvents.Registered …`) |
| `EventRecord` | the case's record, a plain .NET value (a `PersonRegistered`) |
| `EventRecordType` | the record's type, after upcasting (`PersonRegistered`) |
| `EventsUnionType` | the events union it's a case of (`PersonEvents`) |
| `EventTypeName` | the name the record is stored under: its `[<EventType>]` name, or else its type name |
| `EventJson` | the record as JSON, exactly as stored |
| `EventId` | the same for every repeat of the event: give it to the message as its id, so consumers can drop duplicates |
| `AggregateName`, `AggregateId` | the Aggregate that published it (`Person`, `People-1`) |
| `AggregateVersion` | the Aggregate's version after the command that published the event |
| `PositionInCommand` | the event's place among the events its command published, from 0 |
| `RecordedAt` | when the Outbox recorded it, in UTC |
| `CommandTrace` | the trace of the command that published it (an `ActivityContext`). While `PublishAsync` runs, `Activity.Current` is Rosetta's publish span, a child of it: propagate `Activity.Current` to continue the trace through the broker |
| `Attempt` | 1 the first time the event is handed over, more when earlier attempts threw |

Which one to send depends on the broker:

| Your publisher | Sends |
|---|---|
| a typed bus that routes per union (Rebus, MassTransit) | `Event` |
| routing per event, a webhook contract, a mapper | `EventRecord` (and `EventRecordType`) |
| forwarding bytes (raw RabbitMQ, Kafka) | `EventJson`, with `EventTypeName` as its type |

=== "F#"

    ```fsharp
    type YourEventPublisher(channel: IChannel) =
        interface IOutboxPublisher with
            member this.PublishAsync(publication, cancellationToken) =
                // send publication.EventJson as publication.EventTypeName, with publication.EventId as the message id,
                // and return once the broker has it
                …
    ```

    ```fsharp
    builder.UseOutboxWith<RavenDB>() |> ignore
    builder.UseOutboxPublisher<YourEventPublisher>() |> ignore
    // or, on IHostBuilder: UseOutboxPublisher<YourEventPublisher> builder; on IServiceCollection: AddOutboxPublisher<YourEventPublisher> services
    ```

=== "C#"

    ```csharp
    builder.UseOutboxWith<Stores.RavenDB>();
    builder.UseOutboxPublisher<YourEventPublisher>();
    // or services.AddOutboxPublisher<YourEventPublisher>();
    ```

A whole RabbitMQ publisher, and one on Rebus, are in [Recipes](../guides/recipes.md#publish-the-outbox).

- The publisher is a **singleton made by DI**, so its constructor can take services (a connection, a logger).
- **Return only once the broker has the event** (e.g. RabbitMQ's publisher confirms): when `PublishAsync` returns, the
  event is marked published.
- It needs the Outbox: `UseOutboxWith`, before or after it. Without it the host fails to start: "The outbox's publisher
  needs the outbox: add it with `UseOutboxWith` or `AddOutboxWith`." It's added once per service collection.
- It runs in any process that can load the events' assembly (the PL project), with or without Aggregates registered.

### How it publishes

A hosted service, until the application stops:

1. takes or renews the [lease](#one-instance-at-a-time);
2. reads the oldest unpublished events, `PublishBatchSize` at most;
3. decodes each one into its events union (`EventsUnionClrType`, resolved once, then `EventType` and `Data` through the
   upcasters) and publishes them **one at a time, in order**, each `PublishAsync` awaited before the next;
4. marks the published ones (`PublishedAt`).

After a full batch it reads the next at once; otherwise it waits `PublishPollIntervalMilliseconds`.

- **Order**: as recorded (`RecordedAt`, then `Position`), and each Aggregate's events in their `Version` and `Position`
  order, even when the clocks of the app's instances disagree.
- **Failures**: when `PublishAsync` throws, the events published before it are marked, and publishing starts again from
  the one that failed, after 1, 2, 4, 8, 16, then every 30 s. Each failure is logged as an Error, the 5th in a row as
  Critical ("The outbox {Collection} FAILED to publish 5 times in a row …"), and "The outbox {Collection} RECOVERED" once
  it publishes again. It never gives up, and keeps the order: an event that can't be published holds up the ones after
  it until it can.
- **An event that can't be decoded is held**, the same as a failed `PublishAsync`: retried with the same delays, the
  events after it wait, and nothing is skipped or lost. Each attempt is logged as an Error, the 5th in a row as Critical:
  "The outbox {Collection} cannot decode {EventType} as {EventsUnionClrType}: {Reason} …". The reason says what fixes it:
    - a record from before 1.38 has no `EventsUnionClrType` (`OutboxDecodeError.NoEventsUnion`): publish it with 1.37's
      publisher, or delete it (see [Upgrading](#upgrading-from-137));
    - the union doesn't resolve (`UnknownEventsUnion`): its assembly isn't loaded in this process, or the type was
      renamed;
    - the union has no case for the event's type name after the upcasters (`UnknownEventType`): register an upcaster.

    Once the record is fixed or deleted, publishing goes on, in order.
- **At least once**: a crash between publishing an event and marking it publishes it again. The event's `EventId` is
  the same every time: give it to the message as its id, and let consumers ignore one they've seen.
- **Traced and measured**: each publication is a Producer span `{Aggregate} {EventType} publish` that continues the
  command's trace, tagged `rosetta.event.case` (the union case's name), and counts in `rosetta.outbox.published` with
  `rosetta.outcome` = `published`, `failed` (`PublishAsync` threw) or `undecodable` (see
  [Telemetry](../reference/telemetry.md)).

### Upgrading from 1.37

`IOutboxPublisher.PublishAsync` takes an `OutboxPublication` instead of the `OutboxEvent`: move from `event.Data`,
`event.EventType`, `event.Id`, `event.TraceParent` to `EventJson`, `EventTypeName`, `EventId`, `CommandTrace` (or
`Activity.Current`), and drop your own decoding for `Event` or `EventRecord`.

Records written by 1.37 have no `EventsUnionClrType`, so 1.38 can't decode them. **Let 1.37's publisher drain the
Outbox before deploying 1.38.** A record still left holds the publisher, with a Critical log naming the fix: publish it
yourself, or delete it.

### One instance at a time

However many instances of the app run, one publishes at a time: the one holding the Outbox's **lease**, which it
renews before every batch, and every third of a lease during a long one (it stops the batch, marking what it
published, if another instance took the lease meanwhile). The others check every half lease (15 s). An instance that
stops (a deployment) hands the lease over, so another takes over at its next check; after a crash, once the lease
has expired: within `PublishLeaseDuration` (30 s) and a half.

| Store | The lease |
|---|---|
| RavenDB | the document `OutboxLeases/{CollectionName}`, saved on the change vector it was read with |
| MongoDB | a document of the `OutboxLeases` collection, `_id` = `CollectionName` (`owner`, `expiresAt`) |
| PostgreSQL | a row of `rosetta.outbox_leases` (`name` = the Outbox table's name, `owner`, `expires_at`), by the database's clock |

### What's unpublished

The publisher marks events, it doesn't delete them: the Outbox records an event once because it still holds it, so a
repeat of the command doesn't record, and publish, it again. Unpublished means `PublishedAt` is `null`:

| Store | Unpublished | Read with |
|---|---|---|
| RavenDB | `PublishedAt` is `null`. A document without the field, recorded before the publisher was registered, is **not** published: it's taken to be your own publisher's | a query on the collection by `RecordedAt` and `Position`, which waits for its index to be up to date |
| MongoDB | `publishedAt` is `null` (`$type: null`). A document without the field isn't published either | an index on (`publishedAt`, `recordedAt`, `position`), created on first read |
| PostgreSQL | `published_at is null`. The column is added to an existing table (`alter table … add column if not exists`), so **rows recorded before count as unpublished** and are published: delete the ones already sent before registering the publisher | a partial index on (`recorded_at`, `position`) `where published_at is null` |

### Your own publisher

Without `UseOutboxPublisher`, publishing is up to you (another process, a connector of your broker): read the records
in `RecordedAt` and `Position` order, send them, and mark or delete them. The built-in publisher is the recommended way:
it already orders, retries, leases and marks. `OutboxEvents.Decode` reads a recorded event back into its events union,
through the upcasters, as the built-in publisher does:

```fsharp
match OutboxEvents.Decode upcasters recorded with
| Ok event -> ()     // publish it: a PersonEvents case, say
| Error error -> ()  // OutboxDecodeError.NoEventsUnion, UnknownEventsUnion or UnknownEventType; error.Reason says why
```

## Expiry

With `EventTTLMinutes`, each event expires that long after it was recorded, or, **with a publisher**, that long after
it was **published**: an event waiting to be published never expires. The expiry is stored in each event, so a changed
TTL applies to the events recorded (or published) from then on.

- **RavenDB** keeps it in the document's `@expires` metadata (set when it's published, with a publisher), which only
  Document Expiration acts on. The Outbox turns Document Expiration on for the database (once, on first use) when it's
  off, deleting the expired documents every `ExpirationFrequencyMinutes`: 36 hours unless set, the shortest frequency a
  RavenDB Community licence allows, so it works with every licence. An expired event can therefore stay up to that
  long after its TTL. The Outbox leaves an existing Document Expiration configuration alone. In two cases it can't turn
  Document Expiration on, so turn it on yourself (Studio: Settings → Document Expiration):
    - a certificate that can't read the database record can't check it;
    - a licence that doesn't allow the frequency refuses it (`LicenseLimitException`). The Outbox logs a Warning once,
      and the commands go on: the events are recorded, they just aren't deleted until Document Expiration is on.
- **MongoDB** keeps it in an `expiresAt` field (`null` until it's published, with a publisher), which a TTL index the
  Outbox creates on first use acts on. MongoDB deletes expired documents every 60 seconds.
- **PostgreSQL** has no TTL: it keeps the expiry in an `expires_at` column (indexed; `null` until it's published, with a
  publisher), and the Outbox deletes the expired rows itself (`delete … where expires_at < now()`) after recording or
  publishing events, at most every `ExpirationFrequencyMinutes` (and the first time after the process starts). A failed
  deletion is logged as a Warning and fails nothing: the next one tries again after `ExpirationFrequencyMinutes`.

## A custom Outbox store

Register your own singleton `IOutbox` instead of calling `UseOutboxWith`, and every command records its Aggregate's
PublishedEvents in it. To publish it with `UseOutboxPublisher`, it must also implement `IOutboxRelayStore`
(`TryLeaseAsync`, `UnpublishedAsync`, `MarkPublishedAsync`); otherwise the publisher fails when the host starts. See
[Extending Rosetta](../contributing.md#extending-rosetta).
