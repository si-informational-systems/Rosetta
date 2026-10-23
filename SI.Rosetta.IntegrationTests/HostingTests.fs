namespace SI.Rosetta.IntegrationTests

open System
open System.Diagnostics
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Projections
open SI.Rosetta.Projections.HostBuilder

type HostingTests(databases: Databases) =
    [<Fact>]
    member this.``A HostApplicationBuilder app handles commands and projects their events``() =
        task {
            let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
            builder.Configuration.AddConfiguration(databases.Configuration "Rosetta-HostingTests") |> ignore
            let assembly = typeof<ParcelProjection>.Assembly
            builder.UseAggregatesWithEventSourcing<SI.Rosetta.Aggregates.HostBuilder.KurrentDB>(assembly) |> ignore
            builder.UseProjectionsWith<SI.Rosetta.Projections.HostBuilder.KurrentDB, RavenDB>(assembly) |> ignore
            // Aggregates and Projections share the one client
            Assert.Single(builder.Services, fun descriptor -> descriptor.ServiceType = typeof<global.KurrentDB.Client.KurrentDBClient>) |> ignore
            use host = builder.Build()
            do! host.StartAsync()

            let id = sprintf "Parcels-%s" (Guid.NewGuid().ToString("N"))
            let handler = host.Services.GetRequiredService<IParcelAggregateHandler>()
            do! handler.ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            do! handler.ExecuteAsync(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })

            let store = host.Services.GetRequiredService<INoSqlStore>()
            do! Eventually (TimeSpan.FromSeconds 60.0) "the Parcel and ParcelRoute projections catch up" (fun () ->
                    task {
                        let! parcel = store.LoadAsync<Parcel>(id)
                        let! route = store.LoadAsync<ParcelRoute>(sprintf "Routes-%s" id)
                        return not (isNull (box parcel)) && parcel.Recipient = "Grace"
                            && not (isNull (box route)) && route.Recipients = [ "Ada"; "Grace" ]
                    })
            do! host.StopAsync()
        }

    [<Fact>]
    member this.``A command's trace continues in the projections handling its events``() =
        task {
            use listener = new RosettaListener()
            let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
            builder.Configuration.AddConfiguration(databases.Configuration "Rosetta-HostingTests") |> ignore
            let assembly = typeof<ParcelProjection>.Assembly
            builder.UseAggregatesWithEventSourcing<SI.Rosetta.Aggregates.HostBuilder.KurrentDB>(assembly) |> ignore
            builder.UseProjectionsWith<SI.Rosetta.Projections.HostBuilder.KurrentDB, RavenDB>(assembly) |> ignore
            use host = builder.Build()
            // Before the trace starts, so the projections can only continue it through the event's metadata
            do! host.StartAsync()

            let id = sprintf "Parcels-%s" (Guid.NewGuid().ToString("N"))
            let request = (new Activity("Register a parcel")).Start()
            do! host.Services.GetRequiredService<IParcelAggregateHandler>().ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            request.Stop()

            let InTrace () = listener.Activities |> Array.filter (fun activity -> activity.TraceId = request.TraceId)
            let Projected projection = InTrace () |> Array.tryFind (fun activity -> activity.GetTagItem "rosetta.projection" = box projection)
            do! Eventually (TimeSpan.FromSeconds 60.0) "the projection continues the command's trace" (fun () ->
                    task { return (Projected "Parcel").IsSome })

            let command = InTrace () |> Array.find (fun activity -> activity.DisplayName = "Parcel RegisterParcel")
            let projected = (Projected "Parcel").Value
            Assert.Equal("Parcel ParcelRegistered", projected.DisplayName)
            Assert.Equal(ActivityKind.Consumer, projected.Kind)
            Assert.Equal(command.SpanId, projected.ParentSpanId)
            Assert.NotEmpty(listener.Measured "rosetta.projection.events" [ "rosetta.projection", box "Parcel"; "rosetta.outcome", box "handled" ])
            do! host.StopAsync()
        }

    [<Fact>]
    member this.``An app that names its handlers and projections runs only those``() =
        task {
            let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
            builder.Configuration.AddConfiguration(databases.Configuration "Rosetta-HostingTests") |> ignore
            builder.UseAggregatesWithEventSourcing<SI.Rosetta.Aggregates.HostBuilder.KurrentDB>(fun aggregates ->
                aggregates.Handler<ParcelAggregateHandler>()) |> ignore
            builder.UseProjectionsWith<SI.Rosetta.Projections.HostBuilder.KurrentDB, RavenDB>(fun projections ->
                projections.Projection<ParcelProjection, ParcelEvents>()) |> ignore
            // ParcelRouteProjection isn't named, so its handler isn't registered
            Assert.DoesNotContain(builder.Services, fun descriptor -> descriptor.ServiceType = typeof<ParcelRouteProjectionHandler>)
            use host = builder.Build()
            do! host.StartAsync()

            let id = sprintf "Parcels-%s" (Guid.NewGuid().ToString("N"))
            let handler = host.Services.GetRequiredService<IParcelAggregateHandler>()
            do! handler.ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            do! handler.ExecuteAsync(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })

            let store = host.Services.GetRequiredService<INoSqlStore>()
            do! Eventually (TimeSpan.FromSeconds 60.0) "the Parcel projection catches up" (fun () ->
                    task {
                        let! parcel = store.LoadAsync<Parcel>(id)
                        return not (isNull (box parcel)) && parcel.Recipient = "Grace"
                    })
            let! route = store.LoadAsync<ParcelRoute>(sprintf "Routes-%s" id)
            Assert.Null(box route)
            do! host.StopAsync()
        }
