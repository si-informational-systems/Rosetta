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
open SI.Rosetta.Projections.EventStore
open SI.Rosetta.Persistence.RavenDB
open SI.Rosetta.Persistence.MongoDB
open SI.Rosetta.Projections.RavenDB
open SI.Rosetta.Projections.MongoDB

[<AutoOpen>]
module HostBuilderExtensionCommon =
    let HostBuilderExtensionInUse : string = "SI.Rosetta.Projections.UseProjections"
    
    let CreateRavenDocumentStore(config: IConfiguration) = 
        let docStore = 
            RavenDocumentStoreFactory.CreateAndInitializeDocumentStore 
                (RavenConfig.FromConfiguration(config)) 
                None
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
            | t when t = typeof<RavenDB> ->
                if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                    services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(ctx.Configuration)) |> ignore
                services
                    .AddSingleton<INoSqlStore, RavenDbProjectionsStore>()
                    .AddSingleton<ISqlStore, RavenDbProjectionsStore>()
                    .AddTransient<ICheckpointStore, RavenDbCheckpointStore>() |> ignore
            //MONGO DB
            | t when t = typeof<MongoDB> ->
                let mongoConfig = MongoConfig.FromConfiguration(ctx.Configuration)
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
                        sp.GetRequiredService<IHostApplicationLifetime>(),
                        projectionsAssembly
                    )) |> ignore
            | _ -> raise (Exception("Only types extending `EventRepository` can be used as an Event Repository generic option!"))
        )

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
