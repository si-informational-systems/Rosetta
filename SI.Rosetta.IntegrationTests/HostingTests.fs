namespace SI.Rosetta.IntegrationTests

open System
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
            do! host.StopAsync()
        }
