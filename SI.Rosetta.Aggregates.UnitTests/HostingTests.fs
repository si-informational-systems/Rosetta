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

    [<Fact>]
    member this.``Only Rosetta's stores can be used``() =
        // No public constructor: the bases can't be inherited outside Rosetta, or passed as a store (the new() constraint)
        Assert.Empty(typeof<EventSourcedAggregateRepository>.GetConstructors())
        Assert.Empty(typeof<StateBasedAggregateRepository>.GetConstructors())
        for store in [ typeof<KurrentDB>; typeof<RavenDB>; typeof<MongoDB> ] do
            Assert.True(store.IsSealed, sprintf "%s can be inherited" store.Name)

    [<Fact>]
    member this.``A named handler is the only one registered, under its marker interface``() =
        let builder = CreateBuilder()
        let mutable registration = Unchecked.defaultof<AggregatesRegistration>

        builder.UseAggregatesWithEventSourcing<KurrentDB>(fun aggregates ->
            aggregates.Handler<OrganizationAggregateHandler>()
            registration <- aggregates) |> ignore

        Assert.Equal<Type>([ typeof<OrganizationAggregateHandler> ], registration.HandlerTypes)
        let handlers = builder.Services |> Seq.filter (fun descriptor -> typeof<IAggregateHandler>.IsAssignableFrom descriptor.ServiceType)
        Assert.Equal(typeof<OrganizationAggregateHandler>, (Seq.exactlyOne handlers).ImplementationType)
        use host = builder.Build()
        Assert.IsType<OrganizationAggregateHandler>(host.Services.GetRequiredService<IOrganizationAggregateHandler>()) |> ignore

    [<Fact>]
    member this.``A handler both named and found in an assembly is registered once``() =
        let services = ServiceCollection()
        let configuration = CreateBuilder().Configuration

        services.AddAggregatesWithEventSourcing<KurrentDB>(configuration, fun aggregates ->
            aggregates.Handler<OrganizationAggregateHandler>()
            aggregates.FromAssemblyOf<OrganizationAggregateHandler>()
            aggregates.FromAssembly typeof<OrganizationAggregateHandler>.Assembly) |> ignore

        let handlers = services |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<IOrganizationAggregateHandler>)
        Assert.Equal(typeof<OrganizationAggregateHandler>, (Seq.exactlyOne handlers).ImplementationType)

    [<Fact>]
    member this.``A handler without a marker interface can't be named``() =
        let builder = CreateBuilder()

        let error =
            Assert.Throws<InvalidOperationException>(fun () ->
                builder.UseAggregatesWithEventSourcing<KurrentDB>(fun aggregates ->
                    aggregates.Handler<AggregateHandler<OrganizationAggregate, OrganizationCommands>>()) |> ignore)

        Assert.Contains("marker interface", error.Message)

    [<Fact>]
    member this.``Handlers can be named on an IHostBuilder``() =
        let hostBuilder =
            Microsoft.Extensions.Hosting.HostBuilder()
                .ConfigureAppConfiguration(fun configuration ->
                    configuration.AddInMemoryCollection(dict [ "KurrentDB:ConnectionString", "kurrentdb://localhost:1?tls=false" ]) |> ignore)

        hostBuilder.UseAggregatesWithEventSourcing<KurrentDB>(fun aggregates -> aggregates.Handler<OrganizationAggregateHandler>()) |> ignore

        use host = hostBuilder.Build()
        Assert.IsType<OrganizationAggregateHandler>(host.Services.GetRequiredService<IOrganizationAggregateHandler>()) |> ignore
