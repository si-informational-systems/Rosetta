namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Generic
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit
open SI.Rosetta.Projections
open SI.Rosetta.Projections.HostBuilder

type HostingTests() =
    // Nothing connects while the services are only registered and resolved: MongoDB and KurrentDB clients connect on first use
    let CreateBuilder () =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddInMemoryCollection(dict [
            "KurrentDB:ConnectionString", "kurrentdb://localhost:2113?tls=false"
            "MongoDB:Host", "localhost"
            "MongoDB:DatabaseName", "Rosetta-HostingTests" ]) |> ignore
        builder

    [<Fact>]
    member this.``UseProjectionsWith registers the projections on a HostApplicationBuilder``() =
        let builder = CreateBuilder()

        builder.UseProjectionsWith<KurrentDB, MongoDB>(typeof<OrganizationProjection>.Assembly) |> ignore

        use host = builder.Build()
        Assert.Contains(host.Services.GetServices<IHostedService>(), fun service -> service :? KurrentDBProjectionsHostedServiceInstance)
        Assert.NotNull(host.Services.GetRequiredService<INoSqlStore>())
        Assert.NotNull(host.Services.GetRequiredService<ICheckpointStore>())
        Assert.NotNull(host.Services.GetRequiredService<OrganizationProjectionHandler>())

    [<Fact>]
    member this.``Projections can only be added once``() =
        let builder = CreateBuilder()
        builder.Services.AddProjectionsWith<KurrentDB, MongoDB>(builder.Configuration, typeof<OrganizationProjection>.Assembly) |> ignore

        Assert.Throws<InvalidOperationException>(fun () ->
            builder.UseProjectionsWith<KurrentDB, MongoDB>(typeof<OrganizationProjection>.Assembly) |> ignore) |> ignore
