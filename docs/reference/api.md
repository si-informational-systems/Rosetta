---
title: API reference
description: The public types and members of every SI.Rosetta package, with their signatures.
---

# API reference

The public surface applications use, by namespace. Signatures are F#; long generic constraints are abbreviated.
Members marked *(infrastructure)* are public for the packages' own wiring and rarely used directly.

## SI.Rosetta.Common

*Package `SI.Rosetta.Common`.*

### Messages

```fsharp
type IAggregateCommands = interface end          // a commands DU
type IAggregateEvents = interface end            // an events DU
type ICustomProjectionEvents = inherit IAggregateEvents   // a Custom Projection's DU
```

### `EventType`

```fsharp
[<AttributeUsage(AttributeTargets.Class)>]
type EventType(name: string) = member Name: string
```

The name an event record is stored under, instead of its type name. An empty name throws `ArgumentException`.

### Upcasting

```fsharp
type StoredEvent =
    { TypeName: string           // the name it's stored under
      Data: JsonObject }         // the record's JSON, parsed per read: change it in place
    member RenameField: oldName: string * newName: string -> StoredEvent   // unchanged when oldName is missing

type IEventUpcaster =
    abstract Upcast: StoredEvent -> StoredEvent

type UnknownEventException(typeName: string, eventsType: Type, streamName: string, eventNumber: uint64) =
    inherit Exception
    member TypeName: string
    member EventsType: Type
    member StreamName: string
    member EventNumber: uint64
```

Register upcasters as singletons: `services.AddSingleton<IEventUpcaster, MyUpcaster>()`. They run in registration
order on every event read from KurrentDB.

### `EventEncoding` (module)

| Member | Signature | |
|---|---|---|
| `JsonOptions` | `JsonSerializerOptions` | the System.Text.Json options events are written and read with (FSharp.SystemTextJson, Newtonsoft-like shapes, enums as strings, case-insensitive) |
| `WriteOptionsAs` | `mutable OptionFormat` | how `Encode` writes F# options: `Plain` (default, the value or `null`) or `Newtonsoft` (`{"Case":"Some","Fields":[v]}`); both are always read |
| `TypeNameOf` | `Type -> string` | a record's stored name: its `[<EventType>]` name or its type name |
| `Encode` | `IAggregateEvents -> string * string` | (stored name, record JSON) |
| `EncodeWith` | `OptionFormat -> IAggregateEvents -> string * string` | the same, with an option format |
| `Decode<'TEvents>` | `IEventUpcaster seq -> string -> string -> Result<'TEvents, string>` | upcasts, then reads into the DU's case; `Error` holds the unknown stored name |
| `DecodeAs` | `Type -> IEventUpcaster seq -> string -> string -> Result<IAggregateEvents, string>` | the same, with the DU type as a value |

A DU with two cases under one stored name throws `InvalidOperationException` on its first decode.

### `RosettaTelemetry` (module)

`Name = "SI.Rosetta"`, `ActivitySource`, `Meter`, the instruments (see [Telemetry](telemetry.md)),
`TraceParentHeader = "traceparent"`, `TraceStateHeader = "tracestate"`.

### Helpers

`PluralizeName: string -> string` (Pluralize.NET; used for MongoDB collection names), `IsUnion`, `GetUnionCaseType`.
`TypeKeys` (`PropertiesOf`, `KeyPropertiesOf`, `KeyOf`, `ValuesOf`): the properties a type is stored by and its key
(its `[<Key>]` properties in order, or `Id`), for read models and PostgreSQL Aggregate states.

## SI.Rosetta.Aggregates

*Package `SI.Rosetta.Aggregates`.*

### `AggregateState<'TEvents>`

```fsharp
[<AbstractClass>]
type AggregateState<'TEvents when 'TEvents :> IAggregateEvents>() =
    member Id: string with get, set              // null until the creating event sets it
    member Version: int64 with get, set          // the number of events applied
    abstract ApplyEvent: ev: 'TEvents -> unit
    interface IAggregateStateInstance<'TEvents>  // Mutate = ApplyEvent + Version + 1
```

### `Aggregate<'TState, 'TCommands>`

```fsharp
[<AbstractClass>]
type Aggregate<'TState, 'TCommands>() =         // 'TState :> IAggregateState, new(); 'TCommands :> IAggregateCommands
    member State: 'TState with get, set
    member Changes: List<IAggregateEvents>       // the events to store
    member PublishedEvents: List<IAggregateEvents>   // the integration events
    member Id: string
    member Version: int64
    member ShouldHandleIdempotency: bool         // State.Version > 0: the Aggregate exists
    abstract Execute: command: 'TCommands -> unit

// extension, compiles only with the state's events DU
member aggregate.Apply(event: 'TEvents) : unit  // State.Mutate event; Changes.Add event
```

### `AggregateHandler<'TAggregate, 'TCommands>`

```fsharp
[<AbstractClass>]
type AggregateHandler<'TAggregate, 'TCommands>() =   // 'TAggregate :> IAggregateInstance<'TCommands>, new()
    member AggregateRepository: IAggregateRepository with get, set   // set it in the constructor
    member PublishedEvents: List<IAggregateEvents> with get, set     // the last command's
    abstract ExecuteAsync: command: 'TCommands -> Task<unit>
    member IdempotentlyCreateAggregate: id: string -> command: 'TCommands -> Task<unit>
    member IdempotentlyUpdateAggregate: id: string -> command: 'TCommands -> Task<unit>   // {Aggregate}DoesNotExist when missing
    interface IAggregateHandler

type IAggregateHandler =
    abstract ExecuteAsync: IAggregateCommands -> Task<unit>
    abstract GetPublishedEvents: unit -> List<IAggregateEvents>
```

Both `Idempotently…` methods load the Aggregate, run `Execute`, and store it when `Version` changed; when it didn't
and the command published events, a repository with an Outbox records them. Each is traced and measured.

### Repositories

```fsharp
type IAggregateRepository =
    abstract StoreAsync<'TState> : aggregate: IAggregate<'TState> -> Task
    abstract GetAsync<'TState> : id: string * ?version: int64 -> Task<'TState option>   // None when missing

type IEventSourcedAggregateRepository = inherit IAggregateRepository   // KurrentDB
type IStateBasedAggregateRepository = inherit IAggregateRepository     // RavenDB, MongoDB, PostgreSQL
```

Only the two marker interfaces are registered in DI. `GetAsync` with a `version` rebuilds an event-sourced state up to
that version.

### Snapshots

```fsharp
[<AttributeUsage(AttributeTargets.Class)>]
type SnapshotAttribute(every: int) =          // on an event-sourced state type; every >= 1
    member Every: int                          // a snapshot each time Version reaches a multiple of it
    member Version: int with get, set          // raise it to ignore the snapshots taken before (0)
```

The `Snapshots` module (`Of`, `IsDue`, `FingerprintOf`, `Serialize`, `Deserialize`) is *(infrastructure)*: what a
store's snapshots share. In `SI.Rosetta.Aggregates.KurrentDB`, `KurrentDBAggregateRepository(client, upcasters, logger)`
(also without the logger, which reports snapshots that couldn't be written) takes them, and
`KurrentDBAggregateRepository.SnapshotStreamOf id` names an Aggregate's snapshot stream (`RosettaSnapshots-{id}`). See
[Snapshots](../concepts/aggregates.md#snapshots).

### Exceptions

```fsharp
type DomainException(?message: string) =
    inherit Exception
    member Name: string                                   // "" unless created with Named
    static member Named: name: string * message: string -> DomainException

type ConcurrencyException(message: string, inner: exn) =        // every store: another write came first
    inherit Exception
    new: message: string -> ConcurrencyException
    static member Created: stateType: Type * id: string * ?inner: exn -> ConcurrencyException
    static member Changed: stateType: Type * id: string * loaded: int64 * ?inner: exn -> ConcurrencyException

val LoadedVersionOf: aggregate: IAggregate -> int64   // Version - Changes.Count: the version it was loaded at
```

### Outbox

```fsharp
type OutboxOptions() =
    member CollectionName: string with get, set          // "Outbox"
    member EventTTL: Nullable<TimeSpan> with get, set     // null: kept until deleted
    member ExpirationFrequency: TimeSpan with get, set    // 36 h; RavenDB and PostgreSQL
    member PublishBatchSize: int with get, set            // 100; with a publisher
    member PublishPollInterval: TimeSpan with get, set    // 1 s; with a publisher
    member PublishLeaseDuration: TimeSpan with get, set   // 30 s; with a publisher
    member ExpiresFromPublication: bool with get, set     // set by registration when there's a publisher
    member Validate: unit -> unit

type OutboxEvent =
    { Id: Guid; Aggregate: string; AggregateId: string; Version: int64; Position: int
      EventType: string; Data: string; RecordedAt: DateTime; TraceParent: string; TraceState: string }

type IOutbox =
    abstract RecordAsync: events: OutboxEvent[] -> Task<int>   // records the ones it doesn't hold; returns how many

type IOutboxPublisher =                                        // yours: sends one event to the broker
    abstract PublishAsync: event: OutboxEvent * cancellationToken: CancellationToken -> Task

type IOutboxRelayStore =                                       // Rosetta's outboxes, as the publisher reads them
    abstract TryLeaseAsync: owner: string * duration: TimeSpan * cancellationToken: CancellationToken -> Task<bool>
    abstract UnpublishedAsync: max: int * cancellationToken: CancellationToken -> Task<OutboxEvent[]>   // oldest first
    abstract MarkPublishedAsync: events: OutboxEvent[] * publishedAt: DateTime * cancellationToken: CancellationToken -> Task
```

`OutboxEvents.Of aggregate` builds an Aggregate's `OutboxEvent`s; `OutboxEvents.IdOf` computes the deterministic id;
`OutboxEvents.InPublishingOrder` sorts events as the publisher sends them.
`OutboxAggregateRepository(repository, outbox)` *(infrastructure)* stores, then records. The store packages'
`RavenDBOutbox`, `MongoDBOutbox` and `PostgreSqlOutbox` implement `IOutbox` and `IOutboxRelayStore`; their
`RavenDBOutboxEvent` and `MongoDBOutboxEvent` records have a `PublishedAt: Nullable<DateTime>`.

## SI.Rosetta.Projections

*Package `SI.Rosetta.Projections`.*

### Declaring a Projection

```fsharp
[<AbstractClass>]
type Projection<'TEvents when 'TEvents :> IAggregateEvents>()   // inherit it; Rosetta never instantiates yours

type IAmHandledBy<'THandler, 'TEvents when 'THandler :> IProjectionHandler<'TEvents>> =
    inherit IProjectionOf<'TEvents>             // one per Handler, in run order

type IProjectInto<'TReadModel> = interface end  // one per read model it writes

type IProjectionHandler<'TEvents when 'TEvents :> IAggregateEvents> =
    abstract Handle: event: 'TEvents * checkpoint: uint64 -> Task
```

| Attribute | Arguments | |
|---|---|---|
| `[<HandlesStream(name)>]` | stream name | required, exactly one |
| `[<ProjectionVersion(n)>]` | int | rebuild when raised; none = 0. Above 0, needs `IProjectInto` or `[<RebuildWithoutClearing>]` |
| `[<RebuildWithoutClearing>]` | — | a rebuild empties nothing (Handlers overwrite whole documents/rows) |
| `[<SkipUnknownEvents>]` | — | log and skip events the DU lacks |
| `[<DisabledProjection>]` | — | don't run it |
| `[<CustomProjectionStream(name)>]` | `cp-…` stream | on an `ICustomProjectionEvents` DU |

### Stores

```fsharp
type IProjectionsStore =
    abstract StoreAsync<'T> : doc: 'T -> Task
    abstract StoreManyAsync<'T> : docs: 'T[] -> Task
    abstract LoadAsync<'T> : id: obj -> Task<'T>                             // null when missing
    abstract LoadManyAsync<'T> : ids: obj[] -> Task<Dictionary<obj, 'T>>
    abstract DeleteAsync<'T> : id: obj -> Task
    abstract DeleteManyAsync<'T> : ids: obj[] -> Task

type INoSqlStore = inherit IProjectionsStore   // RavenDB, MongoDB: keyed by Id (as a string)
type ISqlStore = inherit IProjectionsStore     // PostgreSQL: keyed by Id or [<Key>]s (a tuple)
```

Inside a Handler every call joins the event's transaction; outside, each call writes straight away and the `…Many`
calls are all or nothing.

### Exceptions

```fsharp
type ProjectionException(message, innerException) =   // a Handler failed; inner: AggregateException with its error
    member ProjectionName: string
    member SubscriptionStreamName: string
    member EventTypeName: string
    member Checkpoint: uint64
```

### Infrastructure

For custom stores and tests: `Checkpoint` (`Id`, `Value` one-based, `Version`), `ICheckpointStore`,
`IProjectionTransaction` (`CommitAsync checkpoint`, dispose = rollback), `IProjectionsUnitOfWork` (`PrepareAsync`,
`BeginAsync`, `ClearAsync`), `ProjectionTransaction.Current` (the event's transaction, an `AsyncLocal`),
`ProjectionKeys` (`KeyPropertiesOf`, `KeyOf`, `ValuesOf`; delegates to `TypeKeys`), `ProjectionStoreKinds` (`Documents`, `Sql`, `Of`),
`ProjectionReadModels`, `IProjectionsFactory`/`ProjectionsFactory` (`RebuildIfVersioned`, `RebuildIfReplaced`),
`ISubscription`, `ISubscriptionFactory`, and `ISubscriptionHistory` (`ReplacedSinceAsync checkpoint`: why the read
models built up to it no longer match the stream, or `None`; a subscription that implements it is checked when its
Projection starts, and the Projection rebuilt). See [Extending Rosetta](../contributing.md#extending-rosetta).

## SI.Rosetta.Projections.KurrentDB

```fsharp
[<RequireQualifiedAccess>]
type ProjectionState = Starting | Running | Retrying | Faulted | Stopped

type ProjectionRetries =
    { InitialDelay: TimeSpan; MaxDelay: TimeSpan; FailuresBeforeFaulted: int }
    static member Default: ProjectionRetries   // 1 s, 30 s, 5
    member Delay: attempt: int -> TimeSpan      // 1, 2, 4, 8, 16, then 30 s

type ProjectionStatuses() =                     // a singleton
    member All: IReadOnlyList<ProjectionStatus>
    member TryFind: name: string -> ProjectionStatus option
    [<CLIEvent>] member StateChanged: IEvent<ProjectionStatus>

type ProjectionStatus =                         // read-only
    member Name: string
    member Stream: string
    member State: ProjectionState
    member Checkpoint: uint64
    member ConsecutiveFailures: int
    member LastError: string
    member Since: DateTimeOffset
```

Register your own `ProjectionRetries` before `UseProjectionsWith` to change the backoff. Resolve `ProjectionStatuses`
to show the Projections' state in your own UI.

```fsharp
type CustomProjectionAction =                   // what a Custom Projection needs on startup
    | Create
    | Update of reason: string                  // Faulted, or emit disabled
    | Relink                                    // its query changed (Faulted or not): its cp- stream is linked again from the start
    | UpdateUnverified of error: string         // its stored definition couldn't be read
    | Unchanged

type KurrentDBCustomJSProjectionsFactory(conf, logger, managementClient, client, owner: string) =   // also without owner
    static member DefaultOwner: conf: IConfiguration * applicationName: string -> string
    member RegistryStream: string               // RosettaCustomProjections-{owner}
```

`CustomProjectionSync.Decide query status stored` decides the action *(infrastructure)*. `KurrentDBSubscription`
implements `ISubscriptionHistory` for `cp-` streams.

## SI.Rosetta.Aggregates.HostBuilder

| Member | On | |
|---|---|---|
| `UseAggregatesWithEventSourcing<KurrentDB>` | `IHostApplicationBuilder`, `IHostBuilder` | every Aggregate event-sourced |
| `UseAggregatesWithoutEventSourcing<RavenDB \| MongoDB \| PostgreSQL>` | same | every Aggregate state-based |
| `UseAggregatesWith<KurrentDB, RavenDB \| MongoDB \| PostgreSQL>` | same | both |
| `UseOutboxWith<RavenDB \| MongoDB \| PostgreSQL>(?configure)` | same | the Outbox |
| `UseOutboxPublisher<'TPublisher>` | same | publishes the Outbox with `'TPublisher :> IOutboxPublisher` (a singleton) |
| `AddAggregatesWith…(configuration, …)`, `AddOutboxWith<…>(configuration, ?configure)`, `AddOutboxPublisher<'TPublisher>()` | `IServiceCollection` | the same |

Each takes an `Assembly` or an `Action<AggregatesRegistration>`:

```fsharp
type AggregatesRegistration =
    member Handler<'THandler> : unit -> unit          // registered under its marker interface
    member FromAssembly: Assembly -> unit
    member FromAssemblyOf<'T> : unit -> unit
    member HandlerTypes: IReadOnlyList<Type>
```

F# also has curried functions on `IHostBuilder` (`UseAggregatesWith<…> builder assembly`) and `IServiceCollection`
(`AddAggregatesWith<…> services configuration assembly`). See [Registration](../concepts/registration.md).

## SI.Rosetta.Projections.HostBuilder

| Member | On | |
|---|---|---|
| `UseProjectionsWith<KurrentDB, RavenDB \| MongoDB \| PostgreSQL>` | `IHostApplicationBuilder`, `IHostBuilder` | one store |
| `UseProjectionsWith<KurrentDB, RavenDB \| MongoDB, PostgreSQL>` | `IHostApplicationBuilder` (assembly or function), `IHostBuilder` (function) | documents and tables |
| `AddProjectionsWith<…>(configuration, …)` | `IServiceCollection` | the same |
| `AddRosettaProjections(?name, ?tags)` | `IHealthChecksBuilder` | the health check, `rosetta-projections` by default |

```fsharp
type ProjectionsRegistration =
    member Projection<'TProjection, 'TEvents> : unit -> unit   // with the Handlers its IAmHandledBy declare
    member FromAssembly: Assembly -> unit
    member FromAssemblyOf<'T> : unit -> unit
    member StopApplicationOnFailure: unit -> unit             // a failed Projection stops the app (ExitCode 1)
    member ProjectionTypes: IReadOnlyList<Type>
    member HandlerTypes: IReadOnlyList<Type>
```

## Persistence packages

| Type | Package | |
|---|---|---|
| `KurrentDBFactory.CreateSettings`, `CreateClient` | Persistence.KurrentDB | a `KurrentDBClient` from `KurrentDB:ConnectionString` |
| `KurrentDBServices.AddKurrentDBClients services configuration` | Persistence.KurrentDB | the shared clients, lazily, keeping the app's own |
| `RavenConfig.FromConfiguration`, `RavenDocumentStoreFactory.CreateAndInitializeDocumentStore config (Some conventions)` | Persistence.RavenDB | an initialized `IDocumentStore`, creating the database |
| `MongoConfig.FromConfiguration`, `MongoClientFactory`, `MongoConventions.Register()` | Persistence.MongoDB | the client, and the F# BSON conventions |
| `PostgreSqlConfig.FromConfiguration`, `PostgreSqlDatabase(config)` (`DataSource`, `Schema`, `EnsureCreatedAsync()`) | Persistence.PostgreSQL | Rosetta's own pool; creating the database |

## TestKits

### `SI.Rosetta.Aggregates.TestKit.TestKitBase<'THandler>`

| Member | |
|---|---|
| `Given(params events: IAggregateEvents[])` | the Aggregate's past events (stored under the command's `Id`) |
| `When(command: IAggregateCommands)` | the command to run |
| `Then(params events) : Task<unit>` | the events it must produce |
| `Then(produced: ResizeArray, published: ResizeArray) : Task<unit>` | produced and published events |
| `ThenNoEvents() : Task<unit>` | nothing produced (an idempotent repeat) |
| `ThenError(name) : Task<unit>` | a `DomainException` with that `Name` |
| `abstract ExecuteCommand` | override to build the handler yourself |

### `SI.Rosetta.Projections.TestKit.TestKitBase<'TProjection, 'THandler, 'TEvents>`

| Member | |
|---|---|
| `Given(params events: 'TEvents[]) : Task<unit>` | delivers the events one at a time, in order |
| `Then<'T>(expected: 'T) : Task<unit>` | loads the read model by `expected`'s key and deep-compares it |
| `ProjectionStore: IProjectionsStore` | the in-memory store, to arrange or inspect more |
| `abstract ConfigureServices: IServiceCollection -> unit` | register other Handlers and dependencies |

### `SI.Rosetta.TestKit.UpcastingTestKitBase<'TEvents>`

| Member | |
|---|---|
| `abstract ConfigureServices: IServiceCollection -> unit` | **required**: register the `IEventUpcaster`s |
| `Given(params events: IAggregateEvents[])` | events of any DU, encoded as stored |
| `Then(params expected: 'TEvents[])` | what they must be read as |

### `SI.Rosetta.TestKit.ObjectComparer.DeepCompare(a, b) : string`

`""` when equal, otherwise the differences, one per line. Usable in your own assertions.
