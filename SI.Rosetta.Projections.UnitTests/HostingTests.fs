namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Generic
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Xunit
open KurrentDB.Client
open SI.Rosetta.Projections
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Projections.HostBuilder
open SI.Rosetta.Projections.MongoDB
open SI.Rosetta.Projections.PostgreSQL

type HostingTests() =
    // Nothing connects while the services are only registered and resolved: MongoDB and KurrentDB clients connect on first use
    let CreateBuilder () =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddInMemoryCollection(dict [
            "KurrentDB:ConnectionString", "kurrentdb://localhost:2113?tls=false"
            "MongoDB:Host", "localhost"
            "MongoDB:DatabaseName", "Rosetta-HostingTests"
            "PostgreSQL:ConnectionString", "Host=localhost;Database=Rosetta-HostingTests;Username=postgres;Password=postgres" ]) |> ignore
        builder

    [<Fact>]
    member this.``UseProjectionsWith registers the projections on a HostApplicationBuilder``() =
        let builder = CreateBuilder()

        builder.UseProjectionsWith<KurrentDB, MongoDB>(typeof<OrganizationProjection>.Assembly) |> ignore

        use host = builder.Build()
        Assert.Contains(host.Services.GetServices<IHostedService>(), fun service -> service :? KurrentDBProjectionsHostedServiceInstance)
        Assert.NotNull(host.Services.GetRequiredService<INoSqlStore>())
        Assert.NotNull(host.Services.GetRequiredService<ICheckpointStore>())
        Assert.IsType<MongoDbProjectionsUnitOfWork>(host.Services.GetRequiredService<IProjectionsUnitOfWork>()) |> ignore
        Assert.NotNull(host.Services.GetRequiredService<OrganizationProjectionHandler>())
        // The SQL store is PostgreSQL's
        Assert.Null(host.Services.GetService<ISqlStore>())

    [<Fact>]
    member this.``UseProjectionsWith PostgreSQL registers the SQL store, its checkpoints and transactions``() =
        let builder = CreateBuilder()

        builder.UseProjectionsWith<KurrentDB, PostgreSQL>(typeof<OrganizationProjection>.Assembly) |> ignore

        use host = builder.Build()
        Assert.IsType<PostgreSqlStore>(host.Services.GetRequiredService<ISqlStore>()) |> ignore
        // Documents are RavenDB's and MongoDB's
        Assert.Null(host.Services.GetService<INoSqlStore>())
        Assert.IsType<PostgreSqlCheckpointStore>(host.Services.GetRequiredService<ICheckpointStore>()) |> ignore
        Assert.IsType<PostgreSqlProjectionsUnitOfWork>(host.Services.GetRequiredService<IProjectionsUnitOfWork>()) |> ignore
        Assert.Equal("Rosetta-HostingTests", host.Services.GetRequiredService<PostgreSqlDatabase>().Name)

    [<Fact>]
    member this.``Two stores: documents and PostgreSQL, each with its own checkpoints and transactions``() =
        let builder = CreateBuilder()

        builder.UseProjectionsWith<KurrentDB, MongoDB, PostgreSQL>(typeof<OrganizationProjection>.Assembly) |> ignore

        use host = builder.Build()
        Assert.IsType<MongoDbProjectionsStore>(host.Services.GetRequiredService<INoSqlStore>()) |> ignore
        Assert.IsType<PostgreSqlStore>(host.Services.GetRequiredService<ISqlStore>()) |> ignore
        Assert.IsType<MongoDbProjectionsUnitOfWork>(host.Services.GetRequiredKeyedService<IProjectionsUnitOfWork>(ProjectionStoreKinds.Documents)) |> ignore
        Assert.IsType<PostgreSqlProjectionsUnitOfWork>(host.Services.GetRequiredKeyedService<IProjectionsUnitOfWork>(ProjectionStoreKinds.Sql)) |> ignore
        Assert.IsType<MongoDbCheckpointStore>(host.Services.GetRequiredKeyedService<ICheckpointStore>(ProjectionStoreKinds.Documents)) |> ignore
        Assert.IsType<PostgreSqlCheckpointStore>(host.Services.GetRequiredKeyedService<ICheckpointStore>(ProjectionStoreKinds.Sql)) |> ignore
        // The first store is the default, for a projection whose handlers take no store
        Assert.Same(
            host.Services.GetRequiredKeyedService<IProjectionsUnitOfWork>(ProjectionStoreKinds.Documents),
            host.Services.GetRequiredService<IProjectionsUnitOfWork>())

    [<Fact>]
    member this.``A read model belongs to one projection``() =
        let ex = Assert.Throws<InvalidOperationException>(fun () ->
                    ProjectionReadModels.CheckOneProjectionEach [ typeof<OrganizationProjection>; typeof<SecondOrganizationProjection> ])

        Assert.Contains("Organization is a read model of OrganizationProjection and SecondOrganizationProjection", ex.Message)
        ProjectionReadModels.CheckOneProjectionEach [ typeof<OrganizationProjection> ]

    [<Fact>]
    member this.``PostgreSQL needs a connection string``() =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddInMemoryCollection(dict [ "KurrentDB:ConnectionString", "kurrentdb://localhost:2113?tls=false" ]) |> ignore

        let ex = Assert.Throws<InvalidOperationException>(fun () ->
                    builder.UseProjectionsWith<KurrentDB, PostgreSQL>(typeof<OrganizationProjection>.Assembly) |> ignore)

        Assert.Contains("PostgreSQL:ConnectionString", ex.Message)

    [<Fact>]
    member this.``Every subscription shares one KurrentDBClient``() =
        let builder = CreateBuilder()

        builder.UseProjectionsWith<KurrentDB, MongoDB>(typeof<OrganizationProjection>.Assembly) |> ignore

        let registered (serviceType: Type) =
            builder.Services |> Seq.filter (fun descriptor -> descriptor.ServiceType = serviceType) |> Seq.exactlyOne
        Assert.Equal(ServiceLifetime.Singleton, (registered typeof<KurrentDBClient>).Lifetime)
        Assert.Equal(ServiceLifetime.Singleton, (registered typeof<KurrentDBProjectionManagementClient>).Lifetime)
        use host = builder.Build()
        Assert.Same(host.Services.GetRequiredService<KurrentDBClient>(), host.Services.GetRequiredService<KurrentDBClient>())

    [<Fact>]
    member this.``Projections can only be added once``() =
        let builder = CreateBuilder()
        builder.Services.AddProjectionsWith<KurrentDB, MongoDB>(builder.Configuration, typeof<OrganizationProjection>.Assembly) |> ignore

        Assert.Throws<InvalidOperationException>(fun () ->
            builder.UseProjectionsWith<KurrentDB, MongoDB>(typeof<OrganizationProjection>.Assembly) |> ignore) |> ignore

    [<Fact>]
    member this.``Only Rosetta's stores can be used``() =
        // No public constructor: the bases can't be inherited outside Rosetta, or passed as a store (the new() constraint)
        Assert.Empty(typeof<EventRepository>.GetConstructors())
        Assert.Empty(typeof<ProjectionStore>.GetConstructors())
        Assert.Empty(typeof<DocumentProjectionStore>.GetConstructors())
        Assert.Empty(typeof<SqlProjectionStore>.GetConstructors())
        for store in [ typeof<KurrentDB>; typeof<RavenDB>; typeof<MongoDB>; typeof<PostgreSQL> ] do
            Assert.True(store.IsSealed, sprintf "%s can be inherited" store.Name)
