namespace SI.Rosetta.Aggregates.HostBuilder

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open System
open System.Reflection
open System.Runtime.CompilerServices
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.KurrentDB
open SI.Rosetta.Aggregates.RavenDB
open SI.Rosetta.Aggregates.MongoDB
open Raven.Client.Documents
open MongoDB.Driver
open SI.Rosetta.Persistence.RavenDB
open SI.Rosetta.Persistence.KurrentDB
open SI.Rosetta.Persistence.MongoDB
open SI.Rosetta.Common
open KurrentDB.Client

/// Registered with the event-sourced aggregates, so they're added only once
type internal EventSourcedAggregatesAdded() = class end

/// Registered with the state-based aggregates, so they're added only once
type internal StateBasedAggregatesAdded() = class end

[<AutoOpen>]
module HostBuilderKurrentDBExtensionCommmon =

    let InitializeKurrentDB (config: IConfiguration) =
        let client = KurrentDBFactory.InitializeKurrentDB config
        let kurrentDBRepo = new KurrentDBAggregateRepository(client)
        kurrentDBRepo

[<AutoOpen>]
module HostBuilderRavenDBExtensionCommmon =
    // Custom contract resolver to exclude the AggregateState Version field from serialization
    // (RavenDBAggregateRepository keeps it in the document's @metadata instead)
    type AggregateStateContractResolver() =
        inherit Newtonsoft.Json.Serialization.DefaultContractResolver()

        override __.CreateProperty(memberInfo: Reflection.MemberInfo, memberSerialization: Newtonsoft.Json.MemberSerialization) =
            let property = base.CreateProperty(memberInfo, memberSerialization)

            // Only AggregateState types - other documents sharing the store (e.g. Read Models) keep their Version
            if property.PropertyName = "Version" && typeof<IAggregateState>.IsAssignableFrom(memberInfo.DeclaringType) then
                property.ShouldSerialize <- fun _ -> false
                property.ShouldDeserialize <- fun _ -> false

            property

    let CreateRavenDocumentStore(config: IConfiguration) =
        let docStore =
            RavenDocumentStoreFactory.CreateAndInitializeDocumentStore
                (RavenConfig.FromConfiguration(config))
                (Some (fun conventions ->
                    // Configure serialization to exclude Version field
                    conventions.Serialization <-
                        let serialization = Raven.Client.Json.Serialization.NewtonsoftJson.NewtonsoftJsonSerializationConventions()
                        serialization.CustomizeJsonSerializer <- fun serializer ->
                            serializer.ContractResolver <- new AggregateStateContractResolver()
                        serialization
                        ))
        docStore;

[<AutoOpen>]
module HostBuilderMongoDBExtensionCommmon =
    let RegisterMongoDatabase(services: IServiceCollection, config: IConfiguration) =
        let mongoConfig = MongoConfig.FromConfiguration(config)
        if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IMongoClient>)) then
            services.AddSingleton<IMongoClient>(MongoClientFactory.CreateClientWithoutDatabase(mongoConfig)) |> ignore
        if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IMongoDatabase>)) then
            services.AddSingleton<IMongoDatabase>(fun sp ->
                sp.GetRequiredService<IMongoClient>().GetDatabase(mongoConfig.DatabaseName)) |> ignore
        MongoConventions.Register()

/// The registrations behind every Use…/Add… entry point: they only need the services and the configuration,
/// so they work with IHostBuilder, IHostApplicationBuilder (Host.CreateApplicationBuilder, WebApplication.CreateBuilder)
/// or a plain IServiceCollection
[<AutoOpen>]
module ServiceCollectionExtensions =
    let private EnsureAddedOnce<'Marker when 'Marker : (new : unit -> 'Marker) and 'Marker : not struct>
        (services: IServiceCollection) (kind: string) =
        if services |> Seq.exists (fun descriptor -> descriptor.ServiceType = typeof<'Marker>) then
            raise (InvalidOperationException(
                sprintf "%s aggregates can only be added once: by one of `UseAggregatesWith…` or `AddAggregatesWith…`." kind))
        services.AddSingleton<'Marker>(new 'Marker()) |> ignore

    let private AddEventSourcedRepository<'AggregateRepository> (services: IServiceCollection) (configuration: IConfiguration) =
        EnsureAddedOnce<EventSourcedAggregatesAdded> services "Event-sourced"

        match typeof<'AggregateRepository> with
        | t when t = typeof<KurrentDB> ->
            // The application's one client, shared with Projections when they're in the same host
            KurrentDBServices.AddKurrentDBClients services configuration
            services.AddSingleton<IEventSourcedAggregateRepository>(fun sp ->
                KurrentDBAggregateRepository(sp.GetRequiredService<KurrentDBClient>(), sp.GetServices<IEventUpcaster>())
                :> IEventSourcedAggregateRepository) |> ignore
        | _ -> raise (Exception("Only types extending `EventSourcedAggregateRepository` can be used as an Aggregate Repository generic option!"))

    let private AddStateBasedRepository<'AggregateRepository> (services: IServiceCollection) (configuration: IConfiguration) =
        EnsureAddedOnce<StateBasedAggregatesAdded> services "State-based"

        match typeof<'AggregateRepository> with
        | t when t = typeof<RavenDB> ->
            if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(configuration)) |> ignore
            services.AddSingleton<IStateBasedAggregateRepository, RavenDBAggregateRepository>() |> ignore
        | t when t = typeof<MongoDB> ->
            RegisterMongoDatabase(services, configuration)
            services.AddSingleton<IStateBasedAggregateRepository, MongoDBAggregateRepository>() |> ignore
        | _ -> raise (Exception("Only types extending `StateBasedAggregateRepository` can be used as an Aggregate Repository generic option!"))

    /// Event-sourced aggregates, with the handlers in aggregatesAssembly
    let AddAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> EventSourcedAggregateRepository>
        (services: IServiceCollection) (configuration: IConfiguration) (aggregatesAssembly: Assembly) =
        AddEventSourcedRepository<'AggregateRepository> services configuration
        RegisterAggregateHandlers(services, aggregatesAssembly)
        services

    /// State-based aggregates, with the handlers in aggregatesAssembly
    let AddAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> StateBasedAggregateRepository>
        (services: IServiceCollection) (configuration: IConfiguration) (aggregatesAssembly: Assembly) =
        AddStateBasedRepository<'AggregateRepository> services configuration
        RegisterAggregateHandlers(services, aggregatesAssembly)
        services

    /// Both event-sourced and state-based aggregates, with the handlers in aggregatesAssembly
    let AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (services: IServiceCollection) (configuration: IConfiguration) (aggregatesAssembly: Assembly) =
        AddEventSourcedRepository<'EventSourcedAggregateRepository> services configuration
        AddStateBasedRepository<'TStateBasedAggregateRepository> services configuration
        // Once, for both kinds of aggregates
        RegisterAggregateHandlers(services, aggregatesAssembly)
        services

[<AutoOpen>]
module HostBuilderExtensions =
    let UseAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> EventSourcedAggregateRepository>
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddAggregatesWithEventSourcing<'AggregateRepository> services ctx.Configuration aggregatesAssembly |> ignore)

    let UseAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddAggregatesWithoutEventSourcing<'AggregateRepository> services ctx.Configuration aggregatesAssembly |> ignore)

    let UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository> services ctx.Configuration aggregatesAssembly |> ignore)

// C#-friendly (tupled) extension methods delegating to the F# functions above
[<Extension>]
module CSharp_HostBuilderExtensions =
    [<Extension>]
    let UseAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> EventSourcedAggregateRepository>
        (hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        HostBuilderExtensions.UseAggregatesWithEventSourcing<'AggregateRepository> hostBuilder aggregatesAssembly

    [<Extension>]
    let UseAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        HostBuilderExtensions.UseAggregatesWithoutEventSourcing<'AggregateRepository> hostBuilder aggregatesAssembly

    [<Extension>]
    let UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        HostBuilderExtensions.UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository> hostBuilder aggregatesAssembly

/// Extension methods for IHostApplicationBuilder (Host.CreateApplicationBuilder, WebApplication.CreateBuilder)
/// and IServiceCollection, from C# or F#, delegating to the F# functions above
[<Extension; AbstractClass; Sealed>]
type AggregatesExtensions =
    [<Extension>]
    static member UseAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> EventSourcedAggregateRepository>
        (builder: IHostApplicationBuilder, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWithEventSourcing<'AggregateRepository>
            builder.Services builder.Configuration aggregatesAssembly |> ignore
        builder

    [<Extension>]
    static member UseAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> StateBasedAggregateRepository>
        (builder: IHostApplicationBuilder, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWithoutEventSourcing<'AggregateRepository>
            builder.Services builder.Configuration aggregatesAssembly |> ignore
        builder

    [<Extension>]
    static member UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (builder: IHostApplicationBuilder, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository>
            builder.Services builder.Configuration aggregatesAssembly |> ignore
        builder

    [<Extension>]
    static member AddAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> EventSourcedAggregateRepository>
        (services: IServiceCollection, configuration: IConfiguration, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWithEventSourcing<'AggregateRepository> services configuration aggregatesAssembly

    [<Extension>]
    static member AddAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository :> StateBasedAggregateRepository>
        (services: IServiceCollection, configuration: IConfiguration, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWithoutEventSourcing<'AggregateRepository> services configuration aggregatesAssembly

    [<Extension>]
    static member AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (services: IServiceCollection, configuration: IConfiguration, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository>
            services configuration aggregatesAssembly
