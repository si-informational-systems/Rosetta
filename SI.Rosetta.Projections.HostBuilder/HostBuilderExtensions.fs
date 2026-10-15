namespace SI.Rosetta.Projections.HostBuilder

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open System
open System.Reflection
open System.Runtime.CompilerServices
open Microsoft.Extensions.Logging
open MongoDB.Driver
open Raven.Client.Documents
open SI.Rosetta.Projections
open SI.Rosetta.Projections.KurrentDB
open SI.Rosetta.Persistence.KurrentDB
open SI.Rosetta.Persistence.RavenDB
open SI.Rosetta.Persistence.MongoDB
open SI.Rosetta.Projections.RavenDB
open SI.Rosetta.Projections.MongoDB

/// Registered with the projections, so they're added only once
type internal ProjectionsAdded() = class end

[<AutoOpen>]
module HostBuilderExtensionCommon =
    let CreateRavenDocumentStore(config: IConfiguration) =
        let docStore =
            RavenDocumentStoreFactory.CreateAndInitializeDocumentStore
                (RavenConfig.FromConfiguration(config))
                None
        docStore;

/// The registrations behind every Use…/Add… entry point: they only need the services and the configuration,
/// so they work with IHostBuilder, IHostApplicationBuilder (Host.CreateApplicationBuilder, WebApplication.CreateBuilder)
/// or a plain IServiceCollection
[<AutoOpen>]
module ServiceCollectionExtensions =
    /// The projections in projectionsAssembly, subscribed to 'EventRepository and stored in 'ProjectionsStore
    let AddProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct
        and 'ProjectionsStore :> ProjectionStore
        > (services: IServiceCollection) (configuration: IConfiguration) (projectionsAssembly: Assembly) =
        if services |> Seq.exists (fun descriptor -> descriptor.ServiceType = typeof<ProjectionsAdded>) then
            raise (InvalidOperationException("Projections can only be added once: by `UseProjectionsWith` or `AddProjectionsWith`."))
        services.AddSingleton<ProjectionsAdded>(ProjectionsAdded()) |> ignore

        services.AddTransient<IProjectionsFactory, ProjectionsFactory>() |> ignore
        services.AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>() |> ignore
        RegisterProjectionHandlers(services, projectionsAssembly)

        match typeof<'ProjectionsStore> with
        //RAVEN DB
        | t when t = typeof<RavenDB> ->
            if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(configuration)) |> ignore
            services
                .AddSingleton<INoSqlStore, RavenDbProjectionsStore>()
                .AddSingleton<ISqlStore, RavenDbProjectionsStore>()
                .AddTransient<ICheckpointStore, RavenDbCheckpointStore>() |> ignore
        //MONGO DB
        | t when t = typeof<MongoDB> ->
            let mongoConfig = MongoConfig.FromConfiguration(configuration)
            if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IMongoClient>)) then
                services.AddSingleton<IMongoClient>(MongoClientFactory.CreateClientWithoutDatabase(mongoConfig)) |> ignore
            // Singleton: the Projections and Checkpoint stores depending on it are Singletons too
            if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IMongoDatabase>)) then
                services.AddSingleton<IMongoDatabase>(fun sp ->
                    sp.GetRequiredService<IMongoClient>().GetDatabase(mongoConfig.DatabaseName)) |> ignore
            MongoConventions.Register()

            services
                .AddSingleton<INoSqlStore, MongoDbProjectionsStore>()
                .AddSingleton<ISqlStore, MongoDbProjectionsStore>()
                .AddTransient<ICheckpointStore, MongoDbCheckpointStore>() |> ignore

        | _ -> raise (Exception("Only types extending `ProjectionStore` can be used as a Projections Store generic option!"))

        match typeof<'EventRepository> with
        //KURRENTDB
        | t when t = typeof<KurrentDB> ->
            // One client for every subscription, shared with Aggregates when they're in the same host
            KurrentDBServices.AddKurrentDBClients services configuration
            services
                .AddTransient<ISubscriptionFactory, KurrentDBSubscriptionFactory>()
                .AddTransient<IKurrentDBCustomJSProjectionsFactory, KurrentDBCustomJSProjectionsFactory>() |> ignore
            services.AddHostedService(fun sp ->
                new KurrentDBProjectionsHostedServiceInstance(
                    sp.GetRequiredService<ILogger<KurrentDBProjectionsHostedServiceInstance>>(),
                    sp.GetRequiredService<IProjectionsFactory>(),
                    sp.GetRequiredService<IKurrentDBCustomJSProjectionsFactory>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    projectionsAssembly
                )) |> ignore
        | _ -> raise (Exception("Only types extending `EventRepository` can be used as an Event Repository generic option!"))

        services

[<AutoOpen>]
module HostBuilderExtensions =
    let UseProjectionsWith<'EventRepository, 'ProjectionsStore
    when 'EventRepository : not struct
    and 'EventRepository :> EventRepository
    and 'ProjectionsStore : not struct
    and 'ProjectionsStore :> ProjectionStore
    > (hostBuilder: IHostBuilder) (projectionsAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddProjectionsWith<'EventRepository, 'ProjectionsStore> services ctx.Configuration projectionsAssembly |> ignore)

// C#-friendly (tupled) extension method delegating to the F# function above
[<Extension>]
module CSharp_HostBuilderExtensions =
    [<Extension>]
    let UseProjectionsWith<'EventRepository, 'ProjectionsStore
    when 'EventRepository : not struct
    and 'EventRepository :> EventRepository
    and 'ProjectionsStore : not struct
    and 'ProjectionsStore :> ProjectionStore
    > (hostBuilder: IHostBuilder, projectionsAssembly: Assembly) =
        HostBuilderExtensions.UseProjectionsWith<'EventRepository, 'ProjectionsStore> hostBuilder projectionsAssembly

/// Extension methods for IHostApplicationBuilder (Host.CreateApplicationBuilder, WebApplication.CreateBuilder)
/// and IServiceCollection, from C# or F#, delegating to the F# functions above
[<Extension; AbstractClass; Sealed>]
type ProjectionsExtensions =
    [<Extension>]
    static member UseProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct
        and 'ProjectionsStore :> ProjectionStore>
        (builder: IHostApplicationBuilder, projectionsAssembly: Assembly) =
        ServiceCollectionExtensions.AddProjectionsWith<'EventRepository, 'ProjectionsStore>
            builder.Services builder.Configuration projectionsAssembly |> ignore
        builder

    [<Extension>]
    static member AddProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct
        and 'ProjectionsStore :> ProjectionStore>
        (services: IServiceCollection, configuration: IConfiguration, projectionsAssembly: Assembly) =
        ServiceCollectionExtensions.AddProjectionsWith<'EventRepository, 'ProjectionsStore> services configuration projectionsAssembly
