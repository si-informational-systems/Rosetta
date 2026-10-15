namespace SI.Rosetta.Aggregates.UnitTests

open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder

type HostingTests() =
    // The MongoDB client connects on first use, so nothing connects while the services are only registered
    let CreateBuilder () =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddInMemoryCollection(dict [
            "MongoDB:Host", "localhost"
            "MongoDB:DatabaseName", "Rosetta-HostingTests" ]) |> ignore
        builder

    [<Fact>]
    member this.``UseAggregatesWithoutEventSourcing registers the aggregates on a HostApplicationBuilder``() =
        let builder = CreateBuilder()

        builder.UseAggregatesWithoutEventSourcing<MongoDB>(typeof<OrganizationAggregateHandler>.Assembly) |> ignore

        let handlers = builder.Services |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<IOrganizationAggregateHandler>)
        Assert.Equal(typeof<OrganizationAggregateHandler>, (Seq.exactlyOne handlers).ImplementationType)
        use host = builder.Build()
        Assert.NotNull(host.Services.GetRequiredService<IStateBasedAggregateRepository>())

    [<Fact>]
    member this.``State-based aggregates can only be added once``() =
        let builder = CreateBuilder()
        builder.Services.AddAggregatesWithoutEventSourcing<MongoDB>(builder.Configuration, typeof<OrganizationAggregateHandler>.Assembly) |> ignore

        Assert.Throws<InvalidOperationException>(fun () ->
            builder.UseAggregatesWithoutEventSourcing<MongoDB>(typeof<OrganizationAggregateHandler>.Assembly) |> ignore) |> ignore
