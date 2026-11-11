namespace SI.Rosetta.Aggregates.UnitTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging.Abstractions
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder

/// An outbox in memory, as the relay reads it
type MemoryOutbox() =
    let Events = List<OutboxEvent>()
    let Published = HashSet<Guid>()
    member val LeaseHolder: string = null with get, set
    member val Marked = ConcurrentQueue<Guid>()

    member this.Add(events: OutboxEvent seq) = lock Events (fun () -> Events.AddRange events)

    interface IOutbox with
        member this.RecordAsync(events) =
            this.Add events
            Task.FromResult events.Length

    interface IOutboxRelayStore with
        member this.TryLeaseAsync(owner, duration, _cancellationToken) =
            lock this (fun () ->
                if isNull this.LeaseHolder || this.LeaseHolder = owner then
                    // A lease for no time: handed over
                    this.LeaseHolder <- (if duration = TimeSpan.Zero then null else owner)
                    Task.FromResult true
                else Task.FromResult false)

        member this.UnpublishedAsync(max, _cancellationToken) =
            lock Events (fun () ->
                Task.FromResult(Events |> Seq.filter (fun event -> not (Published.Contains event.Id)) |> Seq.truncate max |> Seq.toArray))

        member this.MarkPublishedAsync(events, _publishedAt, _cancellationToken) =
            lock Events (fun () ->
                for event in events do
                    Published.Add event.Id |> ignore
                    this.Marked.Enqueue event.Id)
            Task.CompletedTask

/// Publishes into a list; fails on the events failOn names, once each
type CollectingPublisher() =
    member val Published = ConcurrentQueue<OutboxEvent>()
    member val FailOnce = ConcurrentDictionary<Guid, bool>()

    interface IOutboxPublisher with
        member this.PublishAsync(event, _cancellationToken) =
            if this.FailOnce.TryRemove(event.Id) |> fst then
                Task.FromException(InvalidOperationException "The broker is down")
            else
                this.Published.Enqueue event
                Task.CompletedTask

type OutboxRelayTests() =
    let Event (aggregateId: string) (version: int64) (position: int) (recordedAt: DateTime) : OutboxEvent =
        { Id = Guid.NewGuid()
          Aggregate = "Organization"
          AggregateId = aggregateId
          Version = version
          Position = position
          EventType = "OrganizationRegistered"
          Data = "{}"
          RecordedAt = recordedAt
          TraceParent = null
          TraceState = null }

    let At (seconds: float) = DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc).AddSeconds seconds

    let Options () = OutboxOptions(PublishPollInterval = TimeSpan.FromMilliseconds 20.0)

    let Until (description: string) (condition: unit -> bool) =
        task {
            let deadline = DateTime.UtcNow.AddSeconds 15.0
            while not (condition ()) do
                if DateTime.UtcNow > deadline then raise (TimeoutException description)
                do! Task.Delay 20
        }

    [<Fact>]
    member this.``Events are published as recorded, each aggregate's in its own order``() =
        // The second instance's clock is behind: version 2 of A looks recorded before version 1
        let a1 = Event "A" 1L 0 (At 2.0)
        let a2 = Event "A" 2L 0 (At 1.0)
        let b1 = Event "B" 1L 0 (At 1.5)
        let a2second = Event "A" 2L 1 (At 1.0)

        let ordered = OutboxEvents.InPublishingOrder [| a1; a2second; b1; a2 |]

        Assert.Equal<(string * int64 * int) list>(
            [ "A", 1L, 0; "A", 2L, 0; "B", 1L, 0; "A", 2L, 1 ],
            ordered |> Array.map (fun event -> event.AggregateId, event.Version, event.Position) |> List.ofArray)

    [<Fact>]
    member this.``The relay publishes the events in order and marks them published``() =
        task {
            let outbox = MemoryOutbox()
            let publisher = CollectingPublisher()
            let events = [ Event "A" 1L 0 (At 0.0); Event "A" 2L 0 (At 1.0); Event "B" 1L 0 (At 2.0) ]
            outbox.Add events
            use relay = new OutboxRelay(outbox, publisher, Options (), NullLogger<OutboxRelay>.Instance)

            do! relay.StartAsync(CancellationToken.None)
            do! Until "the events are marked" (fun () -> outbox.Marked.Count = 3)
            do! relay.StopAsync(CancellationToken.None)

            Assert.Equal<Guid list>(events |> List.map (fun event -> event.Id), publisher.Published |> Seq.map (fun event -> event.Id) |> List.ofSeq)
        }

    [<Fact>]
    member this.``A failed publication marks the events before it, and retries from the one that failed``() =
        task {
            let outbox = MemoryOutbox()
            let publisher = CollectingPublisher()
            let first, failing, third = Event "A" 1L 0 (At 0.0), Event "A" 2L 0 (At 1.0), Event "A" 3L 0 (At 2.0)
            outbox.Add [ first; failing; third ]
            publisher.FailOnce.[failing.Id] <- true
            use relay = new OutboxRelay(outbox, publisher, Options (), NullLogger<OutboxRelay>.Instance)

            do! relay.StartAsync(CancellationToken.None)
            do! Until "the events are marked" (fun () -> outbox.Marked.Count = 3)
            do! relay.StopAsync(CancellationToken.None)

            // The first one marked on its own, before the retry
            Assert.Equal(first.Id, outbox.Marked |> Seq.head)
            Assert.Equal<Guid list>([ first.Id; failing.Id; third.Id ], publisher.Published |> Seq.map (fun event -> event.Id) |> List.ofSeq)
        }

    [<Fact>]
    member this.``Without the lease, the relay publishes nothing``() =
        task {
            let outbox = MemoryOutbox(LeaseHolder = "another instance")
            let publisher = CollectingPublisher()
            outbox.Add [ Event "A" 1L 0 (At 0.0) ]
            use relay = new OutboxRelay(outbox, publisher, Options (), NullLogger<OutboxRelay>.Instance)

            do! relay.StartAsync(CancellationToken.None)
            do! Task.Delay 300
            do! relay.StopAsync(CancellationToken.None)

            Assert.Empty(publisher.Published)
        }

    [<Fact>]
    member this.``An outbox that can't be read by the relay fails when the relay is made``() =
        let recordOnly = { new IOutbox with member _.RecordAsync(events) = Task.FromResult events.Length }

        let refused = Assert.Throws<InvalidOperationException>(fun () ->
            new OutboxRelay(recordOnly, CollectingPublisher(), Options (), NullLogger<OutboxRelay>.Instance) |> ignore)

        Assert.Contains("IOutboxRelayStore", refused.Message)

    [<Fact>]
    member this.``With a publisher, the events expire from their publication``() =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddInMemoryCollection([ KeyValuePair.Create("MongoDB:Host", "localhost"); KeyValuePair.Create("MongoDB:DatabaseName", "Rosetta-OutboxRelayTests") ]) |> ignore
        builder.UseOutboxPublisher<CollectingPublisher>().UseOutboxWith<MongoDB>() |> ignore
        use host = builder.Build()

        host.Services.GetRequiredService<IOutbox>() |> ignore

        Assert.True(host.Services.GetRequiredService<OutboxOptions>().ExpiresFromPublication)

    [<Fact>]
    member this.``The publisher can only be added once, and needs the outbox``() =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.UseOutboxPublisher<CollectingPublisher>() |> ignore

        Assert.Throws<InvalidOperationException>(fun () -> builder.UseOutboxPublisher<CollectingPublisher>() |> ignore) |> ignore
        use host = builder.Build()
        let missing = Assert.Throws<InvalidOperationException>(fun () -> host.Services.GetServices<IHostedService>() |> Seq.toList |> ignore)
        Assert.Contains("UseOutboxWith", missing.Message)

    [<Fact>]
    member this.``The publisher's settings are read from the Outbox section``() =
        let configuration =
            ConfigurationBuilder()
                .AddInMemoryCollection([ KeyValuePair.Create("Outbox:PublishBatchSize", "25"); KeyValuePair.Create("Outbox:PublishPollIntervalMilliseconds", "250") ])
                .Build()

        let options = OutboxConfiguration.Read configuration

        Assert.Equal(25, options.PublishBatchSize)
        Assert.Equal(TimeSpan.FromMilliseconds 250.0, options.PublishPollInterval)

    [<Fact>]
    member this.``The relay hands its lease over when the app stops``() =
        task {
            let outbox = MemoryOutbox()
            use relay = new OutboxRelay(outbox, CollectingPublisher(), Options (), NullLogger<OutboxRelay>.Instance)
            do! relay.StartAsync(CancellationToken.None)
            do! Until "the relay holds the lease" (fun () -> not (isNull outbox.LeaseHolder))

            do! relay.StopAsync(CancellationToken.None)

            Assert.Null(outbox.LeaseHolder)
        }

    [<Fact>]
    member this.``A batch stops once another instance holds the lease, and marks what it published``() =
        task {
            let outbox = MemoryOutbox()
            let events = [ for version in 1L .. 10L -> Event "A" version 0 (At (float version)) ]
            outbox.Add events
            let published = ConcurrentQueue<OutboxEvent>()
            let slow =
                { new IOutboxPublisher with
                    member _.PublishAsync(event, _cancellationToken) =
                        task {
                            published.Enqueue event
                            // Another instance takes the lease after the third
                            if published.Count = 3 then outbox.LeaseHolder <- "another instance"
                            do! Task.Delay 20
                        } :> Task }
            use relay = new OutboxRelay(outbox, slow, OutboxOptions(PublishLeaseDuration = TimeSpan.FromMilliseconds 30.0), NullLogger<OutboxRelay>.Instance)

            do! relay.StartAsync(CancellationToken.None)
            do! Until "the published events are marked" (fun () -> outbox.Marked.Count > 0 && outbox.Marked.Count = published.Count)
            do! Task.Delay 200
            do! relay.StopAsync(CancellationToken.None)

            Assert.InRange(published.Count, 3, 5)
            Assert.Equal<Guid list>(published |> Seq.map (fun event -> event.Id) |> List.ofSeq, outbox.Marked |> List.ofSeq)
        }
