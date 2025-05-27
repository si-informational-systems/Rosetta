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
open Microsoft.Extensions.Logging

[<AutoOpen>]
module HostBuilderExtensionCommon =
    let HostBuilderExtensionInUse : string = "SI.Rosetta.Projections.UseProjections"
    
    let CreateDocumentStore(config: IConfiguration) = 
        let docStore = RavenDocumentStoreFactory.CreateAndInitializeDocumentStore(RavenConfig.FromConfiguration(config));
        docStore;

[<AutoOpen>]
module HostBuilderExtensions =
    let UseProjectionsWith<'AggregateRepository, 'ProjectionsRepository 
    when 'AggregateRepository : not struct 
    and 'AggregateRepository :> EventStore
    and 'ProjectionsRepository : not struct
    and 'ProjectionsRepository :> HostBuilder.RavenDB
    > (hostBuilder: IHostBuilder) (projectionsAssembly: Assembly)  =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderExtensionInUse) then
                raise (InvalidOperationException("`UseProjections` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderExtensionInUse, null)

            if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                services.AddSingleton(CreateDocumentStore(ctx.Configuration)) |> ignore

            services
                .AddSingleton<INoSqlStore, RavenDbProjectionsStore>()
                .AddSingleton<ISqlStore, RavenDbProjectionsStore>()
                .AddTransient<ICheckpointReader, RavenDbCheckpointReader>()
                .AddTransient<ICheckpointWriter, RavenDbCheckpointWriter>()
                .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
                .AddTransient<ISubscriptionFactory, ESSubscriptionFactory>()
                .AddTransient<IProjectionsFactory, ProjectionsFactory>()
                .AddTransient<IESCustomJSProjectionsFactory, ESCustomJSProjectionsFactory>() |> ignore

            RegisterProjectionHandlers(services, projectionsAssembly)

            services.AddHostedService(fun sp -> 
                new EventStoreProjectionsHostedServiceInstance(
                    sp.GetRequiredService<ILogger<EventStoreProjectionsHostedServiceInstance>>(),
                    sp.GetRequiredService<IProjectionsFactory>(),
                    sp.GetRequiredService<IESCustomJSProjectionsFactory>(),
                    projectionsAssembly
                )) |> ignore
        )

[<Extension>]
module CSharp_HostBuilderExtensions =
    [<Extension>]
    let UseProjectionsWith<'AggregateRepository, 'ProjectionsRepository 
    when 'AggregateRepository : not struct 
    and 'AggregateRepository :> EventStore
    and 'ProjectionsRepository : not struct
    and 'ProjectionsRepository :> HostBuilder.RavenDB
    > (hostBuilder: IHostBuilder) (projectionsAssembly: Assembly)  =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderExtensionInUse) then
                raise (InvalidOperationException("`UseProjections` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderExtensionInUse, null)

            if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                services.AddSingleton(CreateDocumentStore(ctx.Configuration)) |> ignore

            services
                .AddSingleton<INoSqlStore, RavenDbProjectionsStore>()
                .AddSingleton<ISqlStore, RavenDbProjectionsStore>()
                .AddTransient<ICheckpointReader, RavenDbCheckpointReader>()
                .AddTransient<ICheckpointWriter, RavenDbCheckpointWriter>()
                .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
                .AddTransient<ISubscriptionFactory, ESSubscriptionFactory>()
                .AddTransient<IProjectionsFactory, ProjectionsFactory>()
                .AddTransient<IESCustomJSProjectionsFactory, ESCustomJSProjectionsFactory>() |> ignore

            RegisterProjectionHandlers(services, projectionsAssembly)

            services.AddHostedService(fun sp -> 
                new EventStoreProjectionsHostedServiceInstance(
                    sp.GetRequiredService<ILogger<EventStoreProjectionsHostedServiceInstance>>(),
                    sp.GetRequiredService<IProjectionsFactory>(),
                    sp.GetRequiredService<IESCustomJSProjectionsFactory>(),
                    projectionsAssembly
                )) |> ignore
        )
