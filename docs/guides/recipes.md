---
title: Recipes
description: Step-by-step answers to the tasks that come up building on Rosetta, with the helpers applications write around it.
---

# Recipes

## Add an Aggregate

1. **Published Language**: a `{X}Commands` DU (`IAggregateCommands`) and an `{X}Events` DU (`IAggregateEvents`),
   `[<RequireQualifiedAccess>]`, each case wrapping one record with an `Id: string`; `[<EventType("…")>]` on every
   event record.
2. **Domain**, in a folder `{Plural}/`:
    - `{X}AggregateState` inheriting `AggregateState<{X}Events>`, with `ApplyEvent` setting `Id` in the creating event;
    - `{X}Aggregate` inheriting `Aggregate<{X}AggregateState, {X}Commands>`, with `Execute`;
    - `I{X}AggregateHandler` inheriting `IAggregateHandler`, and `{X}AggregateHandler` taking
      `IEventSourcedAggregateRepository` (or `IStateBasedAggregateRepository`), routing create commands to
      `IdempotentlyCreateAggregate` and the others to `IdempotentlyUpdateAggregate`.
3. **Ids**: `{Plural}-{id}`, so the category stream is `$ce-{Plural}`.
4. **Projections**: a `{X}Stream` literal (`"$ce-{Plural}"`) in the Projections assembly's `StreamNames.fs`.
5. **Tests**: a `{X}Data.fs` with default commands and events, and a test class per topic.

Nothing else: the host's registration finds the new handler in the assembly.

## Add a command

1. Add the record and the case to `{X}Commands`.
2. Handle it in `Execute`; the compiler (FS0025 as an error, if your build sets it) lists every `match` that misses it.
3. Route it in the handler's `ExecuteAsync`: create or update.
4. Test it: the change, the idempotent repeat (`ThenNoEvents`), and every rule (`ThenError`).

## Add an event

1. Add the record (with `[<EventType("TheName")>]`) and the case to `{X}Events`.
2. Apply it in the state's `ApplyEvent`.
3. Handle it in every Projection of that stream, or mark the Projections that don't need it `[<SkipUnknownEvents>]`.
4. If a Custom Projection should link it, add a case for it to the Custom Projection's DU.
5. **Deploy the Projections before the Aggregates.** A Projection that meets an event its deployed DU doesn't know
   stops (unless `[<SkipUnknownEvents>]`).

## Change an event that's already stored

| Change | Do |
|---|---|
| add a field | add it; old events read it as `null`/`0`/`None`. Make it an `option` if "absent" must be told apart |
| remove a field | remove it; the stored value is ignored |
| rename the record | keep `[<EventType("OldName")>]` on it (add one if it had none) |
| rename a field, change its type, split an event | an [upcaster](../concepts/event-evolution.md#upcasters), tested with the Upcasting TestKit |

Never rewrite stored events, and never reuse a stored name for a different shape.

## Name the business errors once

Every application ends up with the same small module over `DomainException.Named`:

```fsharp
[<AutoOpen>]
module Guards =
    let Reject (name: string) (message: string) : 'a =
        raise (DomainException.Named(name, message))

    let Ensure (condition: bool) (name: string) (message: string) =
        if not condition then Reject name message

    let NotBlank (field: string) (value: string) =
        Ensure (not (String.IsNullOrWhiteSpace value)) "ValidationFailed" $"{field} is required."
```

```fsharp
| ReservationCommands.Cancel cmd ->
    Ensure (this.State.Status <> Completed) "ReservationCompleted" "A completed reservation can't be cancelled."
    if this.State.Status <> Cancelled then this.Emit(ReservationEvents.Cancelled { Id = cmd.Id; … })
```

## Publish every event

When every event of an Aggregate is also an integration event, give it an `Emit`:

```fsharp
type ReservationAggregate() =
    inherit Aggregate<ReservationAggregateState, ReservationCommands>()

    member private this.Emit(event: ReservationEvents) =
        this.PublishedEvents.Add event
        this.Apply event
```

## Keep Aggregates deterministic

An Aggregate reads no clock and does no I/O: everything a rule needs comes in the command. Put who issued it and when
on every command (and copy it to the events), e.g. a `Metadata: MessageMetadata` field with `IssuedBy` and
`TimeIssued`, filled in by the API or the bus handler. Tests then fix "now" in the command, and replaying events
reproduces the same state.

## Handle commands from a message bus

The message handler resolves a **fresh** Aggregate Handler for each attempt (they're transient and hold the last
command's `PublishedEvents`), treats a `DomainException` as an outcome, and retries a `ConcurrencyException`:

```fsharp
let ConflictAttempts = 4

/// Runs the command through a fresh handler; re-runs it when another write to the same Aggregate won the race
let RunAsync (newHandler: unit -> IAggregateHandler) (command: IAggregateCommands) =
    task {
        let mutable published = None
        let mutable attempt = 0
        while published.IsNone do
            attempt <- attempt + 1
            let handler = newHandler ()
            try
                do! handler.ExecuteAsync command
                published <- Some (List.ofSeq (handler.GetPublishedEvents()))
            with :? ConcurrencyException when attempt < ConflictAttempts ->
                do! Task.Delay(Random.Shared.Next(20, 80) * attempt)
        return published.Value
    }
```

```fsharp
try
    let! _ = RunAsync (fun () -> services.GetRequiredService<IReservationAggregateHandler>()) command
    // succeeded
with :? DomainException as rejected ->
    // a rule said no: record or reply rejected.Name; the message is done, don't let the bus retry it
    ()
// anything else propagates, and the bus redelivers the message
```

## Map business errors to HTTP

Name errors consistently and map them by convention, in one place:

| `DomainException.Name` | Status |
|---|---|
| `*DoesNotExist`, `*NotFound` | 404 |
| role errors (`AdminOnly`, `ManagementOnly`…) | 403 |
| `ValidationFailed`, `Invalid*`, `*Required` | 400 |
| anything else (a rule) | 409 |
| `ConcurrencyException` (after retries) | 409 |

## Publish the Outbox

Implement `IOutboxPublisher` for your broker and register it with `UseOutboxPublisher`: Rosetta reads the unpublished
events, hands them to it one at a time in order, retries a failure, marks what's published, and publishes from one
instance at a time (see [Outbox](../concepts/outbox.md#publishing-the-events)). With RabbitMQ (RabbitMQ.Client 7):

```fsharp
open System
open System.Text
open System.Threading.Tasks
open RabbitMQ.Client
open SI.Rosetta.Aggregates

/// Sends each Outbox event to the "events" exchange, routed by its type, and returns once RabbitMQ confirms it
type RabbitMqPublisher(connection: IConnection) =   // the app's IConnection singleton
    let Exchange = "events"
    let mutable Channel: IChannel = null

    interface IOutboxPublisher with
        member this.PublishAsync(event, cancellationToken) =
            task {
                if isNull Channel || Channel.IsClosed then
                    // With confirmation tracking, BasicPublishAsync waits for the broker's ack, and throws on a nack
                    let options = CreateChannelOptions(publisherConfirmationsEnabled = true, publisherConfirmationTrackingEnabled = true)
                    let! channel = connection.CreateChannelAsync(options, cancellationToken)
                    Channel <- channel
                let properties =
                    BasicProperties(
                        MessageId = event.Id.ToString(),   // the same for every repeat: consumers dedupe on it
                        Type = event.EventType,            // the stored name, for EventEncoding.Decode
                        ContentType = "application/json",
                        Persistent = true)
                do! Channel.BasicPublishAsync(Exchange, event.EventType, false, properties, ReadOnlyMemory(Encoding.UTF8.GetBytes event.Data), cancellationToken)
            } :> Task
```

```fsharp
builder.UseOutboxWith<RavenDB>() |> ignore
builder.UseOutboxPublisher<RabbitMqPublisher>() |> ignore
```

- The publisher is called one event at a time, so one channel is enough. When `PublishAsync` throws (RabbitMQ down, a
  nack), the event is published again after a growing delay, and the ones after it wait: the order holds.
- Delivery is **at least once**: dedupe on the message id in consumers. A consumer reads the event back into its DU with
  `EventEncoding.Decode<PersonEvents> upcasters message.Type json`.
- `Outbox:EventTTLMinutes` then deletes events that long after they were **published**; unpublished ones are kept.
- To publish the records yourself instead (a RavenDB data subscription, a MongoDB change stream, a connector of your
  broker), leave `UseOutboxPublisher` out: see [Your own publisher](../concepts/outbox.md#your-own-publisher).

## Serialize events on your bus as Rosetta does

To put events on a message bus in the same JSON KurrentDB and the Outbox hold, serialize with
`EventEncoding.JsonOptions` (System.Text.Json + FSharp.SystemTextJson):

```fsharp
let json = JsonSerializer.Serialize(event, event.GetType(), EventEncoding.JsonOptions)
let back = JsonSerializer.Deserialize(json, eventType, EventEncoding.JsonOptions)
```

`EventEncoding.Encode event` gives the stored name and the record's JSON, and `EventEncoding.Decode<'TEvents>
upcasters name json` reads them back into the DU (through upcasters).

## Change a read model document safely

Load, change, store, and treat a missing document as a bug when the event that creates it comes first in the same
stream:

```fsharp
let ChangeAsync<'T when 'T : null> (store: INoSqlStore) (id: string) (change: 'T -> unit) : Task =
    task {
        let! document = store.LoadAsync<'T>(id)
        if isNull document then failwithf "%s is not in the read model yet" id
        change document
        do! store.StoreAsync(document)
    }
```

Failing makes the Projection retry, and then report itself Faulted, instead of silently losing the change.

## A document the API writes too

The Projection's transaction has no optimistic concurrency: it would save a stale copy over the API's change. For a
document both write:

- don't declare it with `IProjectInto` (a rebuild would delete the API's documents);
- write it from the Handler on a session of your own, with `OptimisticConcurrencyMode.Writes`, retrying on RavenDB's
  `ConcurrencyException`;
- make the change idempotent (the event may be handled twice: the Handler's own session isn't part of the event's
  transaction).

## Data from another Aggregate

A Projection reads only its own stream: another stream's Projection may be ahead or behind. What a read model needs
from another Aggregate travels **in the event**: when making a reservation, the command carries the hub's time zone and
the area's name, and `ReservationMade` stores them. Or use a [Custom Projection](../concepts/custom-projections.md),
which merges several streams into one ordered stream.

## Add a PostgreSQL read model

1. Write the record (`Id` or `[<Key>]` fields).
2. Create its table in the app's migrations: a column per field, `not null` unless `option`, a primary key on the key.
3. Declare it on the Projection with `IProjectInto<…>`; write it from a Handler that takes `ISqlStore`.
4. Run the migrations before the Projections start, e.g. a hosted service registered **before**
   `UseProjectionsWith` (hosted services start in registration order):

```fsharp
services.AddHostedService<AnalyticsMigration>() |> ignore     // creates the tables
builder.UseProjectionsWith<KurrentDB, RavenDB, PostgreSQL>(typeof<ReservationAnalytics>.Assembly) |> ignore
```

Until the tables match, the Projection retries and logs what's missing (`ReadModelTableException`).

## Rebuild a read model

Bump `[<ProjectionVersion(n)>]` on its Projection and deploy. Its `IProjectInto` read models are emptied, the
checkpoint reset, and the stream projected from the start. A changed PostgreSQL table needs its migration first:
Rosetta empties tables, it never changes them.

## Change, rename or remove a Custom Projection

- **Add a case** (or change the source streams): deploy. The Custom Projection is relinked from the start of its source
  streams, and the Projections reading its `cp-` stream rebuild their `IProjectInto` read models on their own.
- **Rename its stream**: the new name is a new Custom Projection, and the old one is deleted with its stream. Bump
  `[<ProjectionVersion>]` on the Projections that read it, so they rebuild from the new stream.
- **Remove it**: delete the DU (and its Projections). On the next start the application deletes the KurrentDB
  projection and its `cp-` stream.

Keep the Projections of a `cp-` stream in the application that defines its Custom Projection. See
[Custom Projections](../concepts/custom-projections.md#how-the-javascript-projections-are-deployed).

## Load a long-lived Aggregate faster

Put `[<Snapshot(n)>]` on its state (e.g. every 100 or 500 events), with the state's data in public
`member val … with get, set` properties. When `ApplyEvent` later changes what it builds from the same events, raise the
attribute's `Version` (`[<Snapshot(100, Version = 2)>]`) so the old snapshots are ignored. See
[Snapshots](../concepts/aggregates.md#snapshots).

## Use your own RavenDB conventions

Register your `IDocumentStore` **before** Rosetta; Rosetta's Aggregates, Outbox and Projections then use it:

```fsharp
services.AddSingleton<IDocumentStore>(fun _ ->
    let store = new DocumentStore(Urls = urls, Database = database)
    store.Conventions.FindCollectionName <- fun t -> …
    store.Initialize()) |> ignore
builder.UseAggregatesWithEventSourcing<KurrentDB>(typeof<PersonAggregateHandler>.Assembly) |> ignore
builder.UseOutboxWith<RavenDB>() |> ignore
```

`RavenDocumentStoreFactory.CreateAndInitializeDocumentStore (RavenConfig.FromConfiguration configuration) (Some setup)`
builds one from the `RavenDB` section with your conventions, creating the database when it's missing.

## Test helpers

```fsharp
/// Every event produced is also published (Aggregates with Emit)
let Both (events: IAggregateEvents list) = ResizeArray events, ResizeArray events

type ReservationTests() =
    inherit TestKitBase<ReservationAggregateHandler>()

    [<Fact>]
    member this.``A guest makes a reservation``() =
        task {
            this.Given()
            this.When(ReservationCommands.Make ReservationData.Make)
            let produced, published = Both [ ReservationEvents.Made ReservationData.Made ]
            do! this.Then(produced, published)
        }
```

- A `ReservationData.fs` module holds a default command and event per case; tests change them with `{ x with … }`.
- A `Fixtures.fs` holds ids, a fixed `Now`, and the actors (`Owner`, `Clerk`, `System`).
- Name tests as sentences: ``` ``A guest makes a reservation`` ```.

## Integration tests

Run each service in-process with its `Composition.Configure`, against Testcontainers:

- KurrentDB 26.2 with `KURRENTDB_RUN_PROJECTIONS=All`, `KURRENTDB_START_STANDARD_PROJECTIONS=true`, insecure. Its
  `/health/live` answers **204**, so wait for that status, and give it a start period of a minute on a busy host.
- RavenDB 7.2 unsecured; for data subscriptions bind the **same port inside and outside** the container and set
  `PublicServerUrl` to it, so the advertised TCP address is reachable.
- MongoDB as a single-node replica set; PostgreSQL with your migrations applied.
- A unique database name per run, and an `Eventually` helper that polls a read model until it matches.
