namespace SI.Rosetta.Projections.HostBuilder

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
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
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Projections.RavenDB
open SI.Rosetta.Projections.MongoDB
open SI.Rosetta.Projections.PostgreSQL

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
    /// Registers a store: its store interface (INoSqlStore or ISqlStore), and its checkpoints and transactions keyed by
    /// its kind (ProjectionStoreKinds), which is how ProjectionsFactory finds a projection's. The first store's are also
    /// registered without a key: the default, for a projection whose handlers take no store
    let private AddStore (services: IServiceCollection) (configuration: IConfiguration) (isFirst: bool) (store: Type) =
        let kind = if store = typeof<PostgreSQL> then ProjectionStoreKinds.Sql else ProjectionStoreKinds.Documents
        let register (checkpoints: IServiceProvider -> ICheckpointStore) (unitOfWork: IServiceProvider -> IProjectionsUnitOfWork) =
            services.AddKeyedTransient<ICheckpointStore>(kind, fun sp _ -> checkpoints sp) |> ignore
            // One per store, so PostgreSQL prepares its database once
            services.AddKeyedSingleton<IProjectionsUnitOfWork>(kind, fun sp _ -> unitOfWork sp) |> ignore
            if isFirst then
                services.AddTransient<ICheckpointStore>(fun sp -> checkpoints sp) |> ignore
                services.AddSingleton<IProjectionsUnitOfWork>(fun sp -> sp.GetRequiredKeyedService<IProjectionsUnitOfWork>(kind)) |> ignore
        match store with
        //RAVEN DB
        | t when t = typeof<RavenDB> ->
            if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(configuration)) |> ignore
            services.AddSingleton<INoSqlStore, RavenDbProjectionsStore>() |> ignore
            register
                (fun sp -> RavenDbCheckpointStore(sp.GetRequiredService<IDocumentStore>()))
                (fun sp -> RavenDbProjectionsUnitOfWork(sp.GetRequiredService<IDocumentStore>()))
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
            services.AddSingleton<INoSqlStore, MongoDbProjectionsStore>() |> ignore
            register
                (fun sp -> MongoDbCheckpointStore(sp.GetRequiredService<IMongoDatabase>()))
                // Its transactions need a replica set: the projections don't start without one
                (fun sp -> MongoDbProjectionsUnitOfWork(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<IMongoDatabase>()))
        //POSTGRESQL
        | t when t = typeof<PostgreSQL> ->
            // Read now, so a missing connection string fails at registration
            let postgreSqlConfig = PostgreSqlConfig.FromConfiguration(configuration)
            // Rosetta's own data source, created on first use and disposed with the container
            services.TryAddSingleton<PostgreSqlDatabase>(fun _ -> new PostgreSqlDatabase(postgreSqlConfig))
            services.AddSingleton<ISqlStore, PostgreSqlStore>() |> ignore
            register
                (fun sp -> PostgreSqlCheckpointStore(sp.GetRequiredService<PostgreSqlDatabase>()))
                // Creates a missing database and the checkpoints table, and checks the read model tables, when a projection starts
                (fun sp -> PostgreSqlProjectionsUnitOfWork(sp.GetRequiredService<PostgreSqlDatabase>()))
        | _ -> raise (Exception("Only types extending `ProjectionStore` can be used as a Projections Store generic option!"))

    /// The projections registration names, subscribed to eventRepository and stored in stores (one store, or a document
    /// store and PostgreSQL)
    let internal AddProjectionsTo (services: IServiceCollection) (configuration: IConfiguration) (registration: ProjectionsRegistration)
                                  (eventRepository: Type) (stores: Type list) =
        if services |> Seq.exists (fun descriptor -> descriptor.ServiceType = typeof<ProjectionsAdded>) then
            raise (InvalidOperationException("Projections can only be added once: by `UseProjectionsWith` or `AddProjectionsWith`."))
        services.AddSingleton<ProjectionsAdded>(ProjectionsAdded()) |> ignore
        // The projections that run: each read model belongs to one of them
        ProjectionReadModels.CheckOneProjectionEach registration.ProjectionTypes
        // Each one that can be rebuilt has its read models to empty first
        ProjectionReadModels.CheckRebuildable registration.ProjectionTypes
        // Each [<ClusterWideTransaction>] names only its projection's events
        for projectionType in registration.ProjectionTypes do
            ClusterWideTransaction.Check(projectionType, ProjectionTypes.EventsTypeOf projectionType)

        services.AddTransient<IProjectionsFactory, ProjectionsFactory>() |> ignore
        services.AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>() |> ignore
        for handlerType in registration.HandlerTypes do
            services.AddTransient(handlerType) |> ignore

        stores |> List.iteri (fun i store -> AddStore services configuration (i = 0) store)

        match eventRepository with
        //KURRENTDB
        | t when t = typeof<KurrentDB> ->
            // One client for every subscription, shared with Aggregates when they're in the same host
            KurrentDBServices.AddKurrentDBClients services configuration
            // Every projection's status, for the "rosetta-projections" health check, and when a failed one retries
            services.TryAddSingleton<ProjectionStatuses>()
            services.TryAddSingleton<ProjectionRetries>(ProjectionRetries.Default)
            services
                .AddTransient<ISubscriptionFactory, KurrentDBSubscriptionFactory>()
                // Owned by the application: it deletes only the custom projections it created
                .AddTransient<IKurrentDBCustomJSProjectionsFactory>(fun sp ->
                    let applicationName =
                        match sp.GetService<IHostEnvironment>() with
                        | null -> null
                        | environment -> environment.ApplicationName
                    KurrentDBCustomJSProjectionsFactory(
                        configuration,
                        sp.GetRequiredService<ILogger<KurrentDBCustomJSProjectionsFactory>>(),
                        sp.GetRequiredService<KurrentDB.Client.KurrentDBProjectionManagementClient>(),
                        sp.GetRequiredService<KurrentDB.Client.KurrentDBClient>(),
                        KurrentDBCustomJSProjectionsFactory.DefaultOwner(configuration, applicationName)) :> IKurrentDBCustomJSProjectionsFactory) |> ignore
            services.AddHostedService(fun sp ->
                new KurrentDBProjectionsHostedServiceInstance(
                    sp.GetRequiredService<ILogger<KurrentDBProjectionsHostedServiceInstance>>(),
                    sp.GetRequiredService<IProjectionsFactory>(),
                    sp.GetRequiredService<IKurrentDBCustomJSProjectionsFactory>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    registration,
                    sp.GetRequiredService<ProjectionStatuses>(),
                    sp.GetRequiredService<ProjectionRetries>()
                )) |> ignore
        | _ -> raise (Exception("Only types extending `EventRepository` can be used as an Event Repository generic option!"))

        services

    /// The projections registration names, subscribed to 'EventRepository and stored in 'ProjectionsStore
    let internal AddProjections<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
        and 'ProjectionsStore :> ProjectionStore
        > (services: IServiceCollection) (configuration: IConfiguration) (registration: ProjectionsRegistration) =
        AddProjectionsTo services configuration registration typeof<'EventRepository> [ typeof<'ProjectionsStore> ]

    /// The projections registration names, subscribed to 'EventRepository: the ones whose handlers take INoSqlStore
    /// stored in 'NoSqlStore, the ones whose handlers take ISqlStore in 'SqlStore
    let internal AddProjectionsToBoth<'EventRepository, 'NoSqlStore, 'SqlStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'NoSqlStore : not struct and 'NoSqlStore : (new : unit -> 'NoSqlStore)
        and 'NoSqlStore :> DocumentProjectionStore
        and 'SqlStore : not struct and 'SqlStore : (new : unit -> 'SqlStore)
        and 'SqlStore :> SqlProjectionStore
        > (services: IServiceCollection) (configuration: IConfiguration) (registration: ProjectionsRegistration) =
        AddProjectionsTo services configuration registration typeof<'EventRepository> [ typeof<'NoSqlStore>; typeof<'SqlStore> ]

    /// The projections in projectionsAssembly, subscribed to 'EventRepository and stored in 'ProjectionsStore
    let AddProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
        and 'ProjectionsStore :> ProjectionStore
        > (services: IServiceCollection) (configuration: IConfiguration) (projectionsAssembly: Assembly) =
        AddProjections<'EventRepository, 'ProjectionsStore> services configuration (ProjectionsRegistration.OfAssembly projectionsAssembly)

[<AutoOpen>]
module HostBuilderExtensions =
    let UseProjectionsWith<'EventRepository, 'ProjectionsStore
    when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
    and 'EventRepository :> EventRepository
    and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
    and 'ProjectionsStore :> ProjectionStore
    > (hostBuilder: IHostBuilder) (projectionsAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddProjectionsWith<'EventRepository, 'ProjectionsStore> services ctx.Configuration projectionsAssembly |> ignore)

// C#-friendly (tupled) extension method delegating to the F# function above
[<Extension>]
module CSharp_HostBuilderExtensions =
    [<Extension>]
    let UseProjectionsWith<'EventRepository, 'ProjectionsStore
    when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
    and 'EventRepository :> EventRepository
    and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
    and 'ProjectionsStore :> ProjectionStore
    > (hostBuilder: IHostBuilder, projectionsAssembly: Assembly) =
        HostBuilderExtensions.UseProjectionsWith<'EventRepository, 'ProjectionsStore> hostBuilder projectionsAssembly

/// Extension methods for IHostApplicationBuilder (Host.CreateApplicationBuilder, WebApplication.CreateBuilder)
/// and IServiceCollection, from C# or F#, delegating to the F# functions above. The ones that take an
/// Action<ProjectionsRegistration> name the projections (projections.Projection<…, …>()) or the assemblies to find them
/// in (projections.FromAssemblyOf<…>()), on IHostBuilder too
[<Extension; AbstractClass; Sealed>]
type ProjectionsExtensions =
    [<Extension>]
    static member UseProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
        and 'ProjectionsStore :> ProjectionStore>
        (builder: IHostApplicationBuilder, projectionsAssembly: Assembly) =
        ServiceCollectionExtensions.AddProjectionsWith<'EventRepository, 'ProjectionsStore>
            builder.Services builder.Configuration projectionsAssembly |> ignore
        builder

    [<Extension>]
    static member AddProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
        and 'ProjectionsStore :> ProjectionStore>
        (services: IServiceCollection, configuration: IConfiguration, projectionsAssembly: Assembly) =
        ServiceCollectionExtensions.AddProjectionsWith<'EventRepository, 'ProjectionsStore> services configuration projectionsAssembly

    /// The projections register names, subscribed to 'EventRepository and stored in 'ProjectionsStore
    [<Extension>]
    static member UseProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
        and 'ProjectionsStore :> ProjectionStore>
        (builder: IHostApplicationBuilder, register: Action<ProjectionsRegistration>) =
        ServiceCollectionExtensions.AddProjections<'EventRepository, 'ProjectionsStore>
            builder.Services builder.Configuration (ProjectionsRegistration.Of register) |> ignore
        builder

    /// The projections register names, subscribed to 'EventRepository and stored in 'ProjectionsStore
    [<Extension>]
    static member UseProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
        and 'ProjectionsStore :> ProjectionStore>
        (hostBuilder: IHostBuilder, register: Action<ProjectionsRegistration>) =
        // Named now, so a mistake fails here and not when the host is built
        let registration = ProjectionsRegistration.Of register
        hostBuilder.ConfigureServices(fun ctx services ->
            ServiceCollectionExtensions.AddProjections<'EventRepository, 'ProjectionsStore> services ctx.Configuration registration |> ignore)

    /// The projections register names, subscribed to 'EventRepository and stored in 'ProjectionsStore
    [<Extension>]
    static member AddProjectionsWith<'EventRepository, 'ProjectionsStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'ProjectionsStore : not struct and 'ProjectionsStore : (new : unit -> 'ProjectionsStore)
        and 'ProjectionsStore :> ProjectionStore>
        (services: IServiceCollection, configuration: IConfiguration, register: Action<ProjectionsRegistration>) =
        ServiceCollectionExtensions.AddProjections<'EventRepository, 'ProjectionsStore>
            services configuration (ProjectionsRegistration.Of register)

    // Two stores: the projections whose handlers take INoSqlStore are stored in 'NoSqlStore (RavenDB or MongoDB), the
    // ones whose handlers take ISqlStore in 'SqlStore (PostgreSQL). Each projection writes to one of them

    /// The projections in projectionsAssembly, in a document store and PostgreSQL
    [<Extension>]
    static member UseProjectionsWith<'EventRepository, 'NoSqlStore, 'SqlStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'NoSqlStore : not struct and 'NoSqlStore : (new : unit -> 'NoSqlStore)
        and 'NoSqlStore :> DocumentProjectionStore
        and 'SqlStore : not struct and 'SqlStore : (new : unit -> 'SqlStore)
        and 'SqlStore :> SqlProjectionStore>
        (builder: IHostApplicationBuilder, projectionsAssembly: Assembly) =
        ServiceCollectionExtensions.AddProjectionsToBoth<'EventRepository, 'NoSqlStore, 'SqlStore>
            builder.Services builder.Configuration (ProjectionsRegistration.OfAssembly projectionsAssembly) |> ignore
        builder

    /// The projections register names, in a document store and PostgreSQL
    [<Extension>]
    static member UseProjectionsWith<'EventRepository, 'NoSqlStore, 'SqlStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'NoSqlStore : not struct and 'NoSqlStore : (new : unit -> 'NoSqlStore)
        and 'NoSqlStore :> DocumentProjectionStore
        and 'SqlStore : not struct and 'SqlStore : (new : unit -> 'SqlStore)
        and 'SqlStore :> SqlProjectionStore>
        (builder: IHostApplicationBuilder, register: Action<ProjectionsRegistration>) =
        ServiceCollectionExtensions.AddProjectionsToBoth<'EventRepository, 'NoSqlStore, 'SqlStore>
            builder.Services builder.Configuration (ProjectionsRegistration.Of register) |> ignore
        builder

    /// The projections register names, in a document store and PostgreSQL
    [<Extension>]
    static member UseProjectionsWith<'EventRepository, 'NoSqlStore, 'SqlStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'NoSqlStore : not struct and 'NoSqlStore : (new : unit -> 'NoSqlStore)
        and 'NoSqlStore :> DocumentProjectionStore
        and 'SqlStore : not struct and 'SqlStore : (new : unit -> 'SqlStore)
        and 'SqlStore :> SqlProjectionStore>
        (hostBuilder: IHostBuilder, register: Action<ProjectionsRegistration>) =
        let registration = ProjectionsRegistration.Of register
        hostBuilder.ConfigureServices(fun ctx services ->
            ServiceCollectionExtensions.AddProjectionsToBoth<'EventRepository, 'NoSqlStore, 'SqlStore> services ctx.Configuration registration |> ignore)

    /// The projections in projectionsAssembly, in a document store and PostgreSQL
    [<Extension>]
    static member AddProjectionsWith<'EventRepository, 'NoSqlStore, 'SqlStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'NoSqlStore : not struct and 'NoSqlStore : (new : unit -> 'NoSqlStore)
        and 'NoSqlStore :> DocumentProjectionStore
        and 'SqlStore : not struct and 'SqlStore : (new : unit -> 'SqlStore)
        and 'SqlStore :> SqlProjectionStore>
        (services: IServiceCollection, configuration: IConfiguration, projectionsAssembly: Assembly) =
        ServiceCollectionExtensions.AddProjectionsToBoth<'EventRepository, 'NoSqlStore, 'SqlStore>
            services configuration (ProjectionsRegistration.OfAssembly projectionsAssembly)

    /// The projections register names, in a document store and PostgreSQL
    [<Extension>]
    static member AddProjectionsWith<'EventRepository, 'NoSqlStore, 'SqlStore
        when 'EventRepository : not struct and 'EventRepository : (new : unit -> 'EventRepository)
        and 'EventRepository :> EventRepository
        and 'NoSqlStore : not struct and 'NoSqlStore : (new : unit -> 'NoSqlStore)
        and 'NoSqlStore :> DocumentProjectionStore
        and 'SqlStore : not struct and 'SqlStore : (new : unit -> 'SqlStore)
        and 'SqlStore :> SqlProjectionStore>
        (services: IServiceCollection, configuration: IConfiguration, register: Action<ProjectionsRegistration>) =
        ServiceCollectionExtensions.AddProjectionsToBoth<'EventRepository, 'NoSqlStore, 'SqlStore>
            services configuration (ProjectionsRegistration.Of register)
