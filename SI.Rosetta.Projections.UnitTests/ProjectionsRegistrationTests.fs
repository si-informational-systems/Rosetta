namespace SI.Rosetta.Projections.UnitTests

open System
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit
open SI.Rosetta.Common
open SI.Rosetta.Projections
open SI.Rosetta.Projections.HostBuilder
open SI.Rosetta.Projections.TestKit

[<DisabledProjection>]
[<HandlesStream(ProjectionStreamNames.OrganizationStream)>]
type DisabledOrganizationProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<OrganizationProjectionHandler, OrganizationEvents>

[<RequireQualifiedAccess>]
type UnrelatedEvents =
    | Registered of OrganizationRegistered
    interface IAggregateEvents

type ProjectionsRegistrationTests() =
    // Nothing connects while the services are only registered and resolved: MongoDB and KurrentDB clients connect on first use
    let CreateBuilder () =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddInMemoryCollection(dict [
            "KurrentDB:ConnectionString", "kurrentdb://localhost:2113?tls=false"
            "MongoDB:Host", "localhost"
            "MongoDB:DatabaseName", "Rosetta-HostingTests" ]) |> ignore
        builder

    // What creating a projection needs, with the TestKit's subscriptions and checkpoints
    let CreateFactory () =
        let services = ServiceCollection()
        services
            .AddTransient<OrganizationProjectionHandler>()
            .AddSingleton<INoSqlStore>(TestKitProjectionStore())
            .AddTransient<ICheckpointStore, StubCheckpointStore>()
            .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
            .AddTransient<ISubscriptionFactory, TestKitSubscriptionFactory>()
            .AddTransient<IProjectionsFactory, ProjectionsFactory>() |> ignore
        services.BuildServiceProvider().GetRequiredService<IProjectionsFactory>()

    [<Fact>]
    member this.``A named projection runs with the handlers it declares``() =
        let builder = CreateBuilder()
        let mutable registration = Unchecked.defaultof<ProjectionsRegistration>

        builder.UseProjectionsWith<KurrentDB, MongoDB>(fun projections ->
            projections.Projection<OrganizationProjection, OrganizationEvents>()
            registration <- projections) |> ignore

        Assert.Equal<Type>([ typeof<OrganizationProjection> ], registration.ProjectionTypes)
        Assert.Equal<Type>([ typeof<OrganizationProjectionHandler> ], registration.HandlerTypes)
        use host = builder.Build()
        Assert.Contains(host.Services.GetServices<IHostedService>(), fun service -> service :? KurrentDBProjectionsHostedServiceInstance)
        Assert.NotNull(host.Services.GetRequiredService<OrganizationProjectionHandler>())

    [<Fact>]
    member this.``A projection both named and found in an assembly runs once``() =
        let services = ServiceCollection()
        let mutable registration = Unchecked.defaultof<ProjectionsRegistration>

        services.AddProjectionsWith<KurrentDB, MongoDB>(CreateBuilder().Configuration, fun projections ->
            projections.Projection<OrganizationProjection, OrganizationEvents>()
            projections.FromAssemblyOf<OrganizationProjection>()
            projections.FromAssembly typeof<OrganizationProjection>.Assembly
            registration <- projections) |> ignore

        Assert.Equal<Type>([ typeof<OrganizationProjection> ], registration.ProjectionTypes)
        let handlers = services |> Seq.filter (fun descriptor -> descriptor.ServiceType = typeof<OrganizationProjectionHandler>)
        Assert.Single(handlers) |> ignore

    [<Fact>]
    member this.``A disabled projection doesn't run, even when it's named``() =
        let mutable registration = Unchecked.defaultof<ProjectionsRegistration>

        CreateBuilder().UseProjectionsWith<KurrentDB, MongoDB>(fun projections ->
            projections.Projection<DisabledOrganizationProjection, OrganizationEvents>()
            registration <- projections) |> ignore

        Assert.Empty(registration.ProjectionTypes)

    [<Fact>]
    member this.``Projections can be named on an IHostBuilder``() =
        let hostBuilder =
            Microsoft.Extensions.Hosting.HostBuilder()
                .ConfigureAppConfiguration(fun configuration ->
                    configuration.AddInMemoryCollection(dict [
                        "KurrentDB:ConnectionString", "kurrentdb://localhost:2113?tls=false"
                        "MongoDB:Host", "localhost"
                        "MongoDB:DatabaseName", "Rosetta-HostingTests" ]) |> ignore)

        hostBuilder.UseProjectionsWith<KurrentDB, MongoDB>(fun projections ->
            projections.Projection<OrganizationProjection, OrganizationEvents>()) |> ignore

        use host = hostBuilder.Build()
        Assert.NotNull(host.Services.GetRequiredService<OrganizationProjectionHandler>())

    [<Fact>]
    member this.``The typed factory creates the projection its type declares``() =
        task {
            let! projection = CreateFactory().CreateAsync<OrganizationEvents>(typeof<OrganizationProjection>)

            let instance = projection :?> IProjectionInstance<OrganizationEvents>
            Assert.Equal("Organization", instance.Name)
            Assert.Equal(ProjectionStreamNames.OrganizationStream, instance.SubscriptionStreamName)
            Assert.IsType<OrganizationProjectionHandler>(Seq.exactlyOne instance.Handlers) |> ignore
        }

    [<Fact>]
    member this.``The typed factory refuses a projection of another events DU``() =
        task {
            let! error =
                Assert.ThrowsAsync<ArgumentException>(fun () ->
                    CreateFactory().CreateAsync<UnrelatedEvents>(typeof<OrganizationProjection>) :> Threading.Tasks.Task)

            Assert.Contains("UnrelatedEvents", error.Message)
        }

    [<Fact>]
    member this.``Creating by type alone gives the same projection``() =
        task {
            let! projection = CreateFactory().CreateAsync(typeof<OrganizationProjection>)

            let instance = projection :?> IProjectionInstance<OrganizationEvents>
            Assert.Equal("Organization", instance.Name)
            Assert.IsType<OrganizationProjectionHandler>(Seq.exactlyOne instance.Handlers) |> ignore
        }
