namespace SI.Rosetta.IntegrationTests

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Xunit
open KurrentDB.Client
open SI.Rosetta.Projections
open SI.Rosetta.Projections.KurrentDB
open SI.Rosetta.Projections.TestKit
open SI.Rosetta.Common

type KurrentDBSubscriptionTests(databases: Databases) =
    let Store = TestKitProjectionStore()

    // What UseProjectionsWith<KurrentDB, _> registers for one projection, with the TestKit's in-memory store
    let Services =
        ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(databases.Configuration "unused")
            .AddSingleton<INoSqlStore>(Store)
            .AddTransient<ICheckpointStore, StubCheckpointStore>()
            .AddTransient<ISubscriptionFactory, KurrentDBSubscriptionFactory>()
            .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
            .AddTransient<IProjectionsFactory, ProjectionsFactory>()
            .AddTransient<ParcelProjectionHandler>()
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

            // Without resubscribing: that takes 1 + 2 + 4 + 8 + 16 s before giving up
            let! finished = Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds 15.0))
            cancellation.Cancel()
            Assert.Same(running, finished)
            let ex = Assert.IsType<UnknownEventException>(running.Exception.InnerException)
            Assert.Equal("ParcelLost", ex.TypeName)
            let! recipient = RecipientOf id
            Assert.Equal(Some "Ada", recipient)
        }
