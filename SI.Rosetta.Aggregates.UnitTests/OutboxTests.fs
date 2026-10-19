namespace SI.Rosetta.Aggregates.UnitTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Aggregates.KurrentDB
open SI.Rosetta.Aggregates.MongoDB
open SI.Rosetta.Aggregates.TestKit
open SI.Rosetta.Common

/// Keeps every event it's asked to record, in order, repeats included
type RecordingOutbox() =
    member val Recorded = List<OutboxEvent>()
    interface IOutbox with
        member this.RecordAsync(events) =
            this.Recorded.AddRange events
            Task.FromResult events.Length

/// Fails, as an outbox that's unavailable would
type UnavailableOutbox() =
    interface IOutbox with
        member this.RecordAsync(_events) = Task.FromException<int>(InvalidOperationException "The outbox is unavailable")

type RegisterShipment = { Id: string }
type ShipmentRegistered = { Id: string }

[<RequireQualifiedAccess>]
type ShipmentCommands =
    | Register of RegisterShipment
    interface IAggregateCommands

[<RequireQualifiedAccess>]
type ShipmentEvents =
    | Registered of ShipmentRegistered
    interface IAggregateEvents

type ShipmentAggregateState() =
    inherit AggregateState<ShipmentEvents>()
    override this.ApplyEvent(ev) =
        match ev with
        | ShipmentEvents.Registered ev -> this.Id <- ev.Id

/// Publishes its event in the idempotent branch too, so that a repeat records it again
type ShipmentAggregate() =
    inherit Aggregate<ShipmentAggregateState, ShipmentCommands>()
    override this.Execute(command) =
        match command with
        | ShipmentCommands.Register cmd ->
            let registered = ShipmentEvents.Registered { Id = cmd.Id }
            this.PublishedEvents.Add registered
            if not this.ShouldHandleIdempotency then this.Apply registered

type IShipmentAggregateHandler =
    inherit IAggregateHandler

type ShipmentAggregateHandler(repo: IEventSourcedAggregateRepository) =
    inherit AggregateHandler<ShipmentAggregate, ShipmentCommands>()
    do base.AggregateRepository <- repo
    interface IShipmentAggregateHandler
    override this.ExecuteAsync(command) =
        task {
            match command with
            | ShipmentCommands.Register cmd -> do! this.IdempotentlyCreateAggregate cmd.Id command
        }

/// Fails every store, as a concurrent change would
type ConflictingRepository() =
    interface IAggregateRepository with
        member this.StoreAsync(_aggregate) = Task.FromException(ConcurrencyException "Stored concurrently")
        member this.GetAsync<'TAggregateState
                when 'TAggregateState : (new : unit -> 'TAggregateState) and 'TAggregateState :> IAggregateState>(_id, _version) =
            Task.FromResult(None: 'TAggregateState option)

type OutboxTests() =
    let NewId () = sprintf "Organizations-%s" (Guid.NewGuid().ToString("N"))

    let Execute (command: OrganizationCommands) (aggregate: OrganizationAggregate) =
        (aggregate :> IAggregateInstance<OrganizationCommands>).Execute command
        aggregate

    let Registered id = OrganizationAggregate() |> Execute (OrganizationCommands.Register { Id = id; Name = "Rosetta" })

    let Configuration (settings: (string * string) list) =
        ConfigurationBuilder()
            .AddInMemoryCollection(settings |> Seq.map KeyValuePair.Create)
            .Build() :> IConfiguration

    // The MongoDB and KurrentDB clients connect on first use, so nothing connects while the services are only registered
    let CreateBuilder (settings: (string * string) list) =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddConfiguration(Configuration ([
            "KurrentDB:ConnectionString", "kurrentdb://localhost:1?tls=false"
            "MongoDB:Host", "localhost"
            "MongoDB:DatabaseName", "Rosetta-OutboxTests" ] @ settings)) |> ignore
        builder

    let Assembly = typeof<OrganizationAggregateHandler>.Assembly

    [<Fact>]
    member this.``The settings default to the Outbox collection, kept until deleted``() =
        let options = OutboxConfiguration.Read(Configuration [])

        Assert.Equal("Outbox", options.CollectionName)
        Assert.False(options.EventTTL.HasValue)

    [<Fact>]
    member this.``The settings are read from the Outbox section``() =
        let options = OutboxConfiguration.Read(Configuration [ "Outbox:CollectionName", "IntegrationEvents"; "Outbox:EventTTLMinutes", "90" ])

        Assert.Equal("IntegrationEvents", options.CollectionName)
        Assert.Equal(TimeSpan.FromMinutes 90.0, options.EventTTL.Value)

    [<Theory>]
    [<InlineData("0")>]
    [<InlineData("-5")>]
    [<InlineData("an hour")>]
    member this.``A TTL that isn't a positive number of minutes fails the registration``(minutes: string) =
        let builder = CreateBuilder [ "Outbox:EventTTLMinutes", minutes ]

        Assert.Throws<InvalidOperationException>(fun () -> builder.UseOutboxWith<MongoDB>() |> ignore) |> ignore

    [<Fact>]
    member this.``configure changes the settings read from the configuration``() =
        let builder = CreateBuilder [ "Outbox:CollectionName", "IntegrationEvents"; "Outbox:EventTTLMinutes", "90" ]
        let mutable configured = Unchecked.defaultof<OutboxOptions>

        builder.UseOutboxWith<MongoDB>(fun options ->
            Assert.Equal(TimeSpan.FromMinutes 90.0, options.EventTTL.Value)
            options.EventTTL <- Nullable(TimeSpan.FromDays 7.0)
            configured <- options) |> ignore

        Assert.Equal("IntegrationEvents", configured.CollectionName)
        Assert.Equal(TimeSpan.FromDays 7.0, configured.EventTTL.Value)

    [<Fact>]
    member this.``configure can't leave the collection name empty``() =
        let builder = CreateBuilder []

        Assert.Throws<InvalidOperationException>(fun () ->
            builder.UseOutboxWith<MongoDB>(fun options -> options.CollectionName <- " ") |> ignore) |> ignore

    [<Fact>]
    member this.``The outbox can only be added once``() =
        let builder = CreateBuilder []
        builder.Services.AddOutboxWith<MongoDB>(builder.Configuration) |> ignore

        Assert.Throws<InvalidOperationException>(fun () -> builder.UseOutboxWith<MongoDB>() |> ignore) |> ignore

    [<Fact>]
    member this.``Without an outbox the repositories are the stores' own``() =
        let builder = CreateBuilder []
        builder.UseAggregatesWith<KurrentDB, MongoDB>(Assembly) |> ignore

        use host = builder.Build()

        Assert.IsType<KurrentDBAggregateRepository>(host.Services.GetRequiredService<IEventSourcedAggregateRepository>()) |> ignore
        Assert.IsType<MongoDBAggregateRepository>(host.Services.GetRequiredService<IStateBasedAggregateRepository>()) |> ignore

    [<Fact>]
    member this.``The outbox is behind the event-sourced repository, added before or after the aggregates``() =
        for outboxFirst in [ true; false ] do
            let builder = CreateBuilder []
            if outboxFirst then builder.UseOutboxWith<MongoDB>() |> ignore
            builder.UseAggregatesWithEventSourcing<KurrentDB>(Assembly) |> ignore
            if not outboxFirst then builder.UseOutboxWith<MongoDB>() |> ignore

            use host = builder.Build()

            Assert.IsType<OutboxAggregateRepository>(host.Services.GetRequiredService<IEventSourcedAggregateRepository>()) |> ignore

    [<Fact>]
    member this.``A state-based repository records the events itself when the outbox shares its store``() =
        let builder = CreateBuilder []
        builder.UseAggregatesWithoutEventSourcing<MongoDB>(Assembly).UseOutboxWith<MongoDB>() |> ignore

        use host = builder.Build()

        Assert.IsType<MongoDBAggregateRepository>(host.Services.GetRequiredService<IStateBasedAggregateRepository>()) |> ignore
        Assert.IsType<MongoDBOutbox>(host.Services.GetRequiredService<IOutbox>()) |> ignore

    [<Fact>]
    member this.``An outbox the application registered is the one used``() =
        let builder = CreateBuilder []
        let own = RecordingOutbox()
        builder.Services.AddSingleton<IOutbox>(own) |> ignore
        builder.UseAggregatesWithEventSourcing<KurrentDB>(Assembly) |> ignore

        use host = builder.Build()

        Assert.IsType<OutboxAggregateRepository>(host.Services.GetRequiredService<IEventSourcedAggregateRepository>()) |> ignore
        Assert.Same(own, host.Services.GetRequiredService<IOutbox>())

    [<Fact>]
    member this.``An event published again at the same version has the same Id``() =
        let Registered = """{"Id":"Organizations-1","Name":"Rosetta"}"""
        let IdOf version position eventType data = OutboxEvents.IdOf "Organization" "Organizations-1" version position eventType data
        let id = IdOf 1L 0 "OrganizationRegistered" Registered

        Assert.Equal(id, IdOf 1L 0 "OrganizationRegistered" Registered)
        // UUID v8
        Assert.Equal('8', id.ToString().[14])
        // Anything that tells two events apart gives them different Ids
        Assert.Distinct(
            [ id
              IdOf 2L 0 "OrganizationRegistered" Registered
              IdOf 1L 1 "OrganizationRegistered" Registered
              IdOf 1L 0 "OrganizationNameChanged" Registered
              IdOf 1L 0 "OrganizationRegistered" """{"Id":"Organizations-1","Name":"Renamed"}"""
              OutboxEvents.IdOf "Organization" "Organizations-2" 1L 0 "OrganizationRegistered" Registered
              OutboxEvents.IdOf "Person" "Organizations-1" 1L 0 "OrganizationRegistered" Registered ])

    [<Fact>]
    member this.``The published events are recorded as they're stored, with the command's trace``() =
        let id = NewId()
        let aggregate = Registered id
        use command = (new Activity("RegisterOrganization")).SetIdFormat(ActivityIdFormat.W3C).Start()

        let recorded = OutboxEvents.Of aggregate

        let event = Assert.Single(recorded)
        let eventType, data = EventEncoding.Encode(OrganizationEvents.Registered { Id = id; Name = "Rosetta" })
        Assert.Equal("Organization", event.Aggregate)
        Assert.Equal(id, event.AggregateId)
        Assert.Equal(1L, event.Version)
        Assert.Equal(0, event.Position)
        Assert.Equal(OutboxEvents.IdOf "Organization" id 1L 0 eventType data, event.Id)
        Assert.Equal(eventType, event.EventType)
        Assert.Equal(data, event.Data)
        Assert.Equal(command.Id, event.TraceParent)
        Assert.Equal(DateTimeKind.Utc, event.RecordedAt.Kind)
        match EventEncoding.Decode<OrganizationEvents> Seq.empty event.EventType event.Data with
        | Ok decoded -> Assert.Equal(OrganizationEvents.Registered { Id = id; Name = "Rosetta" }, decoded)
        | Error typeName -> failwithf "%s wasn't read back" typeName

    [<Fact>]
    member this.``Only the published events are recorded, after the aggregate is stored``() =
        task {
            let id = NewId()
            let outbox = RecordingOutbox()
            let events = TestKitAggregateRepository()
            let repository = OutboxAggregateRepository(events, outbox) :> IAggregateRepository
            let aggregate = Registered id |> Execute (OrganizationCommands.ChangeName { Id = id; Name = "Renamed" })

            do! repository.StoreAsync(aggregate)

            Assert.Equal(2, events.GetProducedEvents.Count)
            let recorded = Assert.Single(outbox.Recorded)
            Assert.Equal(fst (EventEncoding.Encode(OrganizationEvents.Registered { Id = id; Name = "Rosetta" })), recorded.EventType)
        }

    [<Fact>]
    member this.``Nothing is recorded when the aggregate isn't stored``() =
        task {
            let outbox = RecordingOutbox()
            let repository = OutboxAggregateRepository(ConflictingRepository(), outbox) :> IAggregateRepository

            let! _ = Assert.ThrowsAsync<ConcurrencyException>(fun () -> repository.StoreAsync(Registered (NewId())))

            Assert.Empty(outbox.Recorded)
        }

    [<Fact>]
    member this.``A handler's command records its published events``() =
        task {
            let id = NewId()
            let outbox = RecordingOutbox()
            let handler = OrganizationAggregateHandler(OutboxAggregateRepository(TestKitAggregateRepository(), outbox)) :> IAggregateHandler

            do! handler.ExecuteAsync(OrganizationCommands.Register { Id = id; Name = "Rosetta" })
            // Idempotent, and publishes nothing: nothing is stored or recorded
            do! handler.ExecuteAsync(OrganizationCommands.ChangeName { Id = id; Name = "" })

            let recorded = Assert.Single(outbox.Recorded)
            Assert.Equal(id, recorded.AggregateId)
        }

    [<Fact>]
    member this.``An idempotent repeat hands the events it publishes again to the outbox, under the same Ids``() =
        task {
            let id = sprintf "Shipments-%s" (Guid.NewGuid().ToString("N"))
            let outbox = RecordingOutbox()
            let events = TestKitAggregateRepository()
            let handler = ShipmentAggregateHandler(OutboxAggregateRepository(events, outbox)) :> IAggregateHandler

            do! handler.ExecuteAsync(ShipmentCommands.Register { Id = id })
            do! handler.ExecuteAsync(ShipmentCommands.Register { Id = id })

            Assert.Single(events.GetProducedEvents) |> ignore
            Assert.Equal<int64 list>([ 1L; 1L ], outbox.Recorded |> Seq.map (fun event -> event.Version) |> List.ofSeq)
            // So the outbox keeps the first one
            Assert.Single(outbox.Recorded |> Seq.distinctBy (fun event -> event.Id)) |> ignore
        }

    [<Fact>]
    member this.``A redelivered command records the events an attempt stored but failed to record``() =
        task {
            let id = sprintf "Shipments-%s" (Guid.NewGuid().ToString("N"))
            // One store for both attempts, as KurrentDB would be
            let events = TestKitAggregateRepository()
            let failing = ShipmentAggregateHandler(OutboxAggregateRepository(events, UnavailableOutbox())) :> IAggregateHandler
            let! _ = Assert.ThrowsAsync<InvalidOperationException>(fun () -> failing.ExecuteAsync(ShipmentCommands.Register { Id = id }) :> Task)
            Assert.Single(events.GetProducedEvents) |> ignore
            let outbox = RecordingOutbox()
            let redelivered = ShipmentAggregateHandler(OutboxAggregateRepository(events, outbox)) :> IAggregateHandler

            do! redelivered.ExecuteAsync(ShipmentCommands.Register { Id = id })

            Assert.Single(events.GetProducedEvents) |> ignore
            let recorded = Assert.Single(outbox.Recorded)
            Assert.Equal(id, recorded.AggregateId)
            Assert.Equal(1L, recorded.Version)
        }

    [<Fact>]
    member this.``Without an outbox an idempotent repeat records nothing, and doesn't fail``() =
        task {
            let id = sprintf "Shipments-%s" (Guid.NewGuid().ToString("N"))
            let handler = ShipmentAggregateHandler(TestKitAggregateRepository()) :> IAggregateHandler

            do! handler.ExecuteAsync(ShipmentCommands.Register { Id = id })
            do! handler.ExecuteAsync(ShipmentCommands.Register { Id = id })

            Assert.Single(handler.GetPublishedEvents()) |> ignore
        }
