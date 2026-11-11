namespace SI.Rosetta.IntegrationTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open KurrentDB.Client
open SI.Rosetta.Projections
open SI.Rosetta.Projections.KurrentDB
open SI.Rosetta.Projections.TestKit
open SI.Rosetta.Common

/// How many more times FlakyParcelProjectionHandler fails
type Flakiness() =
    member val FailuresLeft = 0 with get, set

/// Fails while Flakiness says so, as a handler would while its read model store is down
type FlakyParcelProjectionHandler(store: INoSqlStore, flakiness: Flakiness) =
    interface IProjectionHandler<ParcelEvents> with
        member this.Handle(event, _checkpoint) =
            task {
                if flakiness.FailuresLeft > 0 then
                    flakiness.FailuresLeft <- flakiness.FailuresLeft - 1
                    failwith "the read model store is unavailable"
                match event with
                | ParcelEvents.Registered e -> do! store.StoreAsync({ Id = e.Id; Recipient = e.Recipient } : Parcel)
                | ParcelEvents.Readdressed e -> do! store.StoreAsync({ Id = e.Id; Recipient = e.Recipient } : Parcel)
            }

[<DisabledProjection; HandlesStream("FlakyParcels-1")>]
type FlakyParcelProjection() =
    inherit Projection<ParcelEvents>()
    interface IAmHandledBy<FlakyParcelProjectionHandler, ParcelEvents>

type KurrentDBSubscriptionTests(databases: Databases) =
    let Store = TestKitProjectionStore()
    let Statuses = ProjectionStatuses()
    let Flakiness = Flakiness()
    // Fast, and Faulted from the 2nd failure in a row
    let Retries = { InitialDelay = TimeSpan.FromMilliseconds 10.0; MaxDelay = TimeSpan.FromMilliseconds 50.0; FailuresBeforeFaulted = 2 }

    // What UseProjectionsWith<KurrentDB, _> registers for one projection, with the TestKit's in-memory store
    let Services =
        ServiceCollection()
            .AddLogging()
            .AddSingleton<KurrentDBClient>(databases.KurrentDBClient)
            .AddSingleton<INoSqlStore>(Store)
            .AddTransient<ICheckpointStore, StubCheckpointStore>()
            .AddTransient<ISubscriptionFactory, KurrentDBSubscriptionFactory>()
            .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
            .AddTransient<IProjectionsFactory, ProjectionsFactory>()
            .AddTransient<ParcelProjectionHandler>()
            .AddTransient<FlakyParcelProjectionHandler>()
            .AddSingleton<Flakiness>(Flakiness)
            .AddSingleton<ProjectionStatuses>(Statuses)
            .AddSingleton<ProjectionRetries>(Retries)
            .BuildServiceProvider()

    let Start (projectionType: Type) (cancellationToken: CancellationToken) =
        task {
            let! projection = Services.GetRequiredService<IProjectionsFactory>().CreateAsync(projectionType)
            return projection.StartAsync(cancellationToken)
        }

    let RecipientOf (id: string) =
        task {
            let! parcel = (Store :> IProjectionsStore).LoadAsync<Parcel>(id)
            return if isNull (box parcel) then None else Some parcel.Recipient
        }

    interface IDisposable with
        member _.Dispose() = Services.Dispose()

    [<Fact>]
    member this.``A [<SkipUnknownEvents>] projection skips an event it has no case for and handles the next``() =
        task {
            let id = ParcelStreams.SkippingParcels
            let! _ = databases.KurrentDBClient.AppendToStreamAsync(id, StreamState.NoStream, [
                        Events.Known (ParcelEvents.Registered { Id = id; Recipient = "Ada" })
                        Events.Written "ParcelLost" (sprintf """{"Id":"%s"}""" id)
                        Events.Known (ParcelEvents.Readdressed { Id = id; Recipient = "Grace" }) ])
            use cancellation = new CancellationTokenSource()

            let! running = Start typeof<SkippingParcelProjection> cancellation.Token

            do! Eventually (TimeSpan.FromSeconds 30.0) "the event after the unknown one is projected" (fun () ->
                    task {
                        if running.IsFaulted then running.Wait()
                        let! recipient = RecipientOf id
                        return recipient = Some "Grace"
                    })
            cancellation.Cancel()
            do! running
        }

    [<Fact>]
    member this.``A projection stops at once on an event it has no case for``() =
        task {
            let id = ParcelStreams.StrictParcels
            let! _ = databases.KurrentDBClient.AppendToStreamAsync(id, StreamState.NoStream, [
                        Events.Known (ParcelEvents.Registered { Id = id; Recipient = "Ada" })
                        Events.Written "ParcelLost" (sprintf """{"Id":"%s"}""" id)
                        Events.Known (ParcelEvents.Readdressed { Id = id; Recipient = "Grace" }) ])
            use cancellation = new CancellationTokenSource()

            let! running = Start typeof<StrictParcelProjection> cancellation.Token

            // Without retrying, which would never end
            let! finished = Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds 15.0))
            cancellation.Cancel()
            Assert.Same(running, finished)
            let ex = Assert.IsType<UnknownEventException>(running.Exception.InnerException)
            Assert.Equal("ParcelLost", ex.TypeName)
            let! recipient = RecipientOf id
            Assert.Equal(Some "Ada", recipient)
            Assert.Equal(ProjectionState.Stopped, (Statuses.Of("StrictParcel", id)).State)
        }

    [<Fact>]
    member this.``A projection that keeps failing on an event is Faulted, retries, and recovers``() =
        task {
            let id = "FlakyParcels-1"
            let! _ = databases.KurrentDBClient.AppendToStreamAsync(id, StreamState.NoStream, [
                        Events.Known (ParcelEvents.Registered { Id = id; Recipient = "Ada" })
                        Events.Known (ParcelEvents.Readdressed { Id = id; Recipient = "Grace" }) ])
            let states = Collections.Concurrent.ConcurrentQueue<ProjectionState>()
            use _ = Statuses.StateChanged.Subscribe(fun status -> if status.Name = "FlakyParcel" then states.Enqueue status.State)
            Flakiness.FailuresLeft <- 3
            use cancellation = new CancellationTokenSource()

            let! running = Start typeof<FlakyParcelProjection> cancellation.Token

            do! Eventually (TimeSpan.FromSeconds 30.0) "the events are projected once the handler works again" (fun () ->
                    task {
                        if running.IsFaulted then running.Wait()
                        let! recipient = RecipientOf id
                        return recipient = Some "Grace"
                    })
            Assert.Contains(ProjectionState.Faulted, states)
            do! Eventually (TimeSpan.FromSeconds 10.0) "it runs again" (fun () ->
                    task { return (Statuses.Of("FlakyParcel", id)).State = ProjectionState.Running })
            Assert.False(running.IsCompleted, "the subscription gave up")
            cancellation.Cancel()
            do! running
        }
