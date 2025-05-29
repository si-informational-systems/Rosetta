namespace SI.Rosetta.Projections.HostBuilder

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open System
open System.Reflection
open System.Runtime.CompilerServices
open SI.Rosetta.Projections
open SI.Rosetta.Projections.EventStore
open SI.Stack.RavenDB
open Raven.Client.Documents
open SI.Rosetta.Projections.RavenDB
open SI.Rosetta.Projections.MongoDB
open Microsoft.Extensions.Logging
open MongoDB.Driver
open MongoDB.Bson.Serialization.Conventions;

[<AutoOpen>]
module HostBuilderExtensionCommon =
    let HostBuilderExtensionInUse : string = "SI.Rosetta.Projections.UseProjections"
    
    let CreateRavenDocumentStore(config: IConfiguration) = 
        let docStore = RavenDocumentStoreFactory.CreateAndInitializeDocumentStore(RavenConfig.FromConfiguration(config));
        docStore;

[<AutoOpen>]
module HostBuilderExtensions =
    let UseProjectionsWith<'EventRepository, 'ProjectionsStore 
    when 'EventRepository : not struct 
    and 'EventRepository :> EventRepository
    and 'ProjectionsStore : not struct
    and 'ProjectionsStore :> ProjectionStore
    > (hostBuilder: IHostBuilder) (projectionsAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderExtensionInUse) then
                raise (InvalidOperationException("`UseProjections` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderExtensionInUse, null)

            services.AddTransient<IProjectionsFactory, ProjectionsFactory>() |> ignore
            services.AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>() |> ignore
            RegisterProjectionHandlers(services, projectionsAssembly)
            
            match typeof<'ProjectionsStore> with
            //RAVEN DB
            | t when t = typeof<Raven> ->
                if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                    services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(ctx.Configuration)) |> ignore
                services
                    .AddSingleton<INoSqlStore, RavenDbProjectionsStore>()
                    .AddSingleton<ISqlStore, RavenDbProjectionsStore>()
                    .AddTransient<ICheckpointStore, RavenDbCheckpointStore>() |> ignore
            //MONGO DB
            | t when t = typeof<Mongo> ->
                if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IMongoClient>)) then
                    let connection = SI.Stack.MongoDB.MongoConfig.FromConfiguration(ctx.Configuration)
                    let dbConnection = connection.GenerateConnectionString()
                    let client = new MongoClient(dbConnection)
                    services.AddSingleton<IMongoClient>(client) |> ignore

                    services.AddScoped<IMongoDatabase>(fun sp -> 
                        let client = sp.GetRequiredService<IMongoClient>()
                        let connection = SI.Stack.MongoDB.MongoConfig.FromConfiguration(ctx.Configuration)
                        let dbName = connection.DatabaseName
                        let db = client.GetDatabase(dbName)
                        db
                    ) |> ignore

                    let conventions = ConventionPack()
                    conventions.Add(CamelCaseElementNameConvention())
                    conventions.Add(IgnoreExtraElementsConvention(true))
                    conventions.Add(EnumRepresentationConvention(MongoDB.Bson.BsonType.String))
                    ConventionRegistry.Register("FSharpConventions", conventions, fun _ -> true)

                services
                    .AddSingleton<INoSqlStore, MongoDbProjectionsStore>()
                    .AddSingleton<ISqlStore, MongoDbProjectionsStore>()
                    .AddTransient<ICheckpointStore, MongoDbCheckpointStore>() |> ignore

            | _ -> raise (Exception("Only types extending `ProjectionStore` can be used as a Projections Store generic option!"))

            match typeof<'EventRepository> with
            //EVENT STORE
            | t when t = typeof<EventStore> ->
                services
                    .AddTransient<ISubscriptionFactory, ESSubscriptionFactory>()
                    .AddTransient<IESCustomJSProjectionsFactory, ESCustomJSProjectionsFactory>() |> ignore
                services.AddHostedService(fun sp -> 
                    new EventStoreProjectionsHostedServiceInstance(
                        sp.GetRequiredService<ILogger<EventStoreProjectionsHostedServiceInstance>>(),
                        sp.GetRequiredService<IProjectionsFactory>(),
                        sp.GetRequiredService<IESCustomJSProjectionsFactory>(),
                        projectionsAssembly
                    )) |> ignore
            | _ -> raise (Exception("Only types extending `EventRepository` can be used as an Event Repository generic option!"))
        )

[<Extension>]
module CSharp_HostBuilderExtensions =
    [<Extension>]
    let UseProjectionsWith<'EventRepository, 'ProjectionsStore 
    when 'EventRepository : not struct 
    and 'EventRepository :> EventStore
    and 'ProjectionsStore : not struct
    and 'ProjectionsStore :> HostBuilder.Raven
    > (hostBuilder: IHostBuilder) (projectionsAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderExtensionInUse) then
                raise (InvalidOperationException("`UseProjections` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderExtensionInUse, null)

            services.AddTransient<IProjectionsFactory, ProjectionsFactory>() |> ignore
            services.AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>() |> ignore
            RegisterProjectionHandlers(services, projectionsAssembly)
            
            match typeof<'ProjectionsStore> with
            //RAVEN DB
            | t when t = typeof<Raven> ->
                if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                    services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(ctx.Configuration)) |> ignore
                services
                    .AddSingleton<INoSqlStore, RavenDbProjectionsStore>()
                    .AddSingleton<ISqlStore, RavenDbProjectionsStore>()
                    .AddTransient<ICheckpointStore, RavenDbCheckpointStore>() |> ignore
            //MONGO DB
            | t when t = typeof<Mongo> ->
                if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IMongoClient>)) then
                    let connection = SI.Stack.MongoDB.MongoConfig.FromConfiguration(ctx.Configuration)
                    let dbConnection = connection.GenerateConnectionString()
                    let client = new MongoClient(dbConnection)
                    services.AddSingleton<IMongoClient>(client) |> ignore

                    services.AddScoped<IMongoDatabase>(fun sp -> 
                        let client = sp.GetRequiredService<IMongoClient>()
                        let connection = SI.Stack.MongoDB.MongoConfig.FromConfiguration(ctx.Configuration)
                        let dbName = connection.DatabaseName
                        let db = client.GetDatabase(dbName)
                        db
                    ) |> ignore

                    let conventions = ConventionPack()
                    conventions.Add(CamelCaseElementNameConvention())
                    conventions.Add(IgnoreExtraElementsConvention(true))
                    conventions.Add(EnumRepresentationConvention(MongoDB.Bson.BsonType.String))
                    ConventionRegistry.Register("FSharpConventions", conventions, fun _ -> true)

                services
                    .AddSingleton<INoSqlStore, MongoDbProjectionsStore>()
                    .AddSingleton<ISqlStore, MongoDbProjectionsStore>()
                    .AddTransient<ICheckpointStore, MongoDbCheckpointStore>() |> ignore

            | _ -> raise (Exception("Only types extending `ProjectionStore` can be used as a Projections Store generic option!"))

            match typeof<'EventRepository> with
            //EVENT STORE
            | t when t = typeof<EventStore> ->
                services
                    .AddTransient<ISubscriptionFactory, ESSubscriptionFactory>()
                    .AddTransient<IESCustomJSProjectionsFactory, ESCustomJSProjectionsFactory>() |> ignore
                services.AddHostedService(fun sp -> 
                    new EventStoreProjectionsHostedServiceInstance(
                        sp.GetRequiredService<ILogger<EventStoreProjectionsHostedServiceInstance>>(),
                        sp.GetRequiredService<IProjectionsFactory>(),
                        sp.GetRequiredService<IESCustomJSProjectionsFactory>(),
                        projectionsAssembly
                    )) |> ignore
            | _ -> raise (Exception("Only types extending `EventRepository` can be used as an Event Repository generic option!"))
        )
