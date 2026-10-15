namespace SI.Rosetta.Aggregates.UnitTests

open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit
open KurrentDB.Client
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder

type HostingTests() =
    // The MongoDB and KurrentDB clients connect on first use, so nothing connects while the services are only registered
    let CreateBuilder () =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddInMemoryCollection(dict [
            "KurrentDB:ConnectionString", "kurrentdb://localhost:1?tls=false"
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
    member this.``Event-sourced aggregates share one KurrentDBClient, which registering doesn't connect``() =
        let builder = CreateBuilder()

        builder.UseAggregatesWithEventSourcing<KurrentDB>(typeof<OrganizationAggregateHandler>.Assembly) |> ignore

        let clients = builder.Services |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<KurrentDBClient>)
        Assert.Equal(ServiceLifetime.Singleton, (Seq.exactlyOne clients).Lifetime)
        use host = builder.Build()
        // Nothing listens on the configured port: the client connects only when it's first used
        Assert.NotNull(host.Services.GetRequiredService<IEventSourcedAggregateRepository>())
        Assert.Same(host.Services.GetRequiredService<KurrentDBClient>(), host.Services.GetRequiredService<KurrentDBClient>())

    [<Fact>]
    member this.``A KurrentDBClient the application registered is the one used``() =
        let builder = CreateBuilder()
        use own = new KurrentDBClient(KurrentDBClientSettings.Create "kurrentdb://localhost:1?tls=false")
        builder.Services.AddSingleton<KurrentDBClient>(own) |> ignore

        builder.UseAggregatesWithEventSourcing<KurrentDB>(typeof<OrganizationAggregateHandler>.Assembly) |> ignore

        use host = builder.Build()
        Assert.Same(own, host.Services.GetRequiredService<KurrentDBClient>())

    [<Fact>]
    member this.``State-based aggregates can only be added once``() =
        let builder = CreateBuilder()
        builder.Services.AddAggregatesWithoutEventSourcing<MongoDB>(builder.Configuration, typeof<OrganizationAggregateHandler>.Assembly) |> ignore

        Assert.Throws<InvalidOperationException>(fun () ->
            builder.UseAggregatesWithoutEventSourcing<MongoDB>(typeof<OrganizationAggregateHandler>.Assembly) |> ignore) |> ignore
