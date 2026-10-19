namespace SI.Rosetta.Aggregates.HostBuilder

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open System
open System.Reflection
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
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

/// Registered with the outbox, so it's added only once
type internal OutboxAdded() = class end

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

    /// The application's one IDocumentStore, shared by the state-based aggregates, the outbox and Projections when
    /// they're in the same host. One the application registered first is kept
    let RegisterRavenDocumentStore(services: IServiceCollection, config: IConfiguration) =
        if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
            services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(config)) |> ignore

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

module OutboxConfiguration =
    /// The outbox's settings from the Outbox configuration section: CollectionName (Outbox unless set) and
    /// EventTTLMinutes (no expiry unless set)
    let Read (configuration: IConfiguration) =
        let options = OutboxOptions()
        match configuration.["Outbox:CollectionName"] with
        | name when String.IsNullOrWhiteSpace name -> ()
        | name -> options.CollectionName <- name.Trim()
        match configuration.["Outbox:EventTTLMinutes"] with
        | minutes when String.IsNullOrWhiteSpace minutes -> ()
        | minutes ->
            match Double.TryParse(minutes, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, value when value > 0.0 -> options.EventTTL <- Nullable(TimeSpan.FromMinutes value)
            | _ -> raise (InvalidOperationException(sprintf "Outbox:EventTTLMinutes must be a positive number of minutes, not '%s'." minutes))
        options

/// The registrations behind every Use…/Add… entry point: they only need the services and the configuration,
/// so they work with IHostBuilder, IHostApplicationBuilder (Host.CreateApplicationBuilder, WebApplication.CreateBuilder)
/// or a plain IServiceCollection
[<AutoOpen>]
module ServiceCollectionExtensions =
    let private EnsureAddedOnce<'Marker when 'Marker : (new : unit -> 'Marker) and 'Marker : not struct and 'Marker : (new : unit -> 'Marker)>
        (services: IServiceCollection) (addedTwice: string) =
        if services |> Seq.exists (fun descriptor -> descriptor.ServiceType = typeof<'Marker>) then
            raise (InvalidOperationException addedTwice)
        services.AddSingleton<'Marker>(new 'Marker()) |> ignore

    let private AggregatesAddedTwice (kind: string) =
        sprintf "%s aggregates can only be added once: by one of `UseAggregatesWith…` or `AddAggregatesWith…`." kind

    /// The outbox, when one is registered: by UseOutboxWith/AddOutboxWith, before or after the aggregates, as the
    /// repositories ask for it when they're first resolved. Or the application's own IOutbox
    let private OutboxOf (sp: IServiceProvider) =
        match box (sp.GetService<IOutbox>()) with
        | null -> None
        | outbox -> Some (outbox :?> IOutbox)

    /// The repository, with the outbox behind it when one is registered
    let private WithOutbox<'TRepository when 'TRepository :> IAggregateRepository>
        (sp: IServiceProvider) (repository: IAggregateRepository) : 'TRepository =
        match OutboxOf sp with
        | None -> box repository :?> 'TRepository
        | Some outbox -> box (OutboxAggregateRepository(repository, outbox)) :?> 'TRepository

    let private AddEventSourcedRepository<'AggregateRepository> (services: IServiceCollection) (configuration: IConfiguration) =
        EnsureAddedOnce<EventSourcedAggregatesAdded> services (AggregatesAddedTwice "Event-sourced")

        match typeof<'AggregateRepository> with
        | t when t = typeof<KurrentDB> ->
            // The application's one client, shared with Projections when they're in the same host
            KurrentDBServices.AddKurrentDBClients services configuration
            services.AddSingleton<IEventSourcedAggregateRepository>(fun sp ->
                KurrentDBAggregateRepository(sp.GetRequiredService<KurrentDBClient>(), sp.GetServices<IEventUpcaster>())
                |> WithOutbox<IEventSourcedAggregateRepository> sp) |> ignore
        | _ -> raise (Exception("Only types extending `EventSourcedAggregateRepository` can be used as an Aggregate Repository generic option!"))

    let private AddStateBasedRepository<'AggregateRepository> (services: IServiceCollection) (configuration: IConfiguration) =
        EnsureAddedOnce<StateBasedAggregatesAdded> services (AggregatesAddedTwice "State-based")

        match typeof<'AggregateRepository> with
        // An outbox in the same store records the events with the state, in one transaction
        | t when t = typeof<RavenDB> ->
            RegisterRavenDocumentStore(services, configuration)
            services.AddSingleton<IStateBasedAggregateRepository>(fun sp ->
                let store = sp.GetRequiredService<IDocumentStore>()
                match OutboxOf sp with
                | Some (:? RavenDBOutbox as outbox) -> RavenDBAggregateRepository(store, Some outbox) :> IStateBasedAggregateRepository
                | _ -> RavenDBAggregateRepository(store) |> WithOutbox<IStateBasedAggregateRepository> sp) |> ignore
        | t when t = typeof<MongoDB> ->
            RegisterMongoDatabase(services, configuration)
            services.AddSingleton<IStateBasedAggregateRepository>(fun sp ->
                let db = sp.GetRequiredService<IMongoDatabase>()
                match OutboxOf sp with
                | Some (:? MongoDBOutbox as outbox) -> MongoDBAggregateRepository(db, Some outbox) :> IStateBasedAggregateRepository
                | _ -> MongoDBAggregateRepository(db) |> WithOutbox<IStateBasedAggregateRepository> sp) |> ignore
        | _ -> raise (Exception("Only types extending `StateBasedAggregateRepository` can be used as an Aggregate Repository generic option!"))

    /// Event-sourced aggregates, with the handlers in aggregatesAssembly
    let AddAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> EventSourcedAggregateRepository>
        (services: IServiceCollection) (configuration: IConfiguration) (aggregatesAssembly: Assembly) =
        AddEventSourcedRepository<'AggregateRepository> services configuration
        RegisterAggregateHandlers(services, aggregatesAssembly)
        services

    /// State-based aggregates, with the handlers in aggregatesAssembly
    let AddAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> StateBasedAggregateRepository>
        (services: IServiceCollection) (configuration: IConfiguration) (aggregatesAssembly: Assembly) =
        AddStateBasedRepository<'AggregateRepository> services configuration
        RegisterAggregateHandlers(services, aggregatesAssembly)
        services

    /// Both event-sourced and state-based aggregates, with the handlers in aggregatesAssembly
    let AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct and 'EventSourcedAggregateRepository : (new : unit -> 'EventSourcedAggregateRepository)
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct and 'TStateBasedAggregateRepository : (new : unit -> 'TStateBasedAggregateRepository)
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (services: IServiceCollection) (configuration: IConfiguration) (aggregatesAssembly: Assembly) =
        AddEventSourcedRepository<'EventSourcedAggregateRepository> services configuration
        AddStateBasedRepository<'TStateBasedAggregateRepository> services configuration
        // Once, for both kinds of aggregates
        RegisterAggregateHandlers(services, aggregatesAssembly)
        services

    /// The outbox in 'OutboxStore, with the Outbox configuration section's settings, which configure may change
    let internal AddOutbox<'OutboxStore> (services: IServiceCollection) (configuration: IConfiguration) (configure: Action<OutboxOptions>) =
        EnsureAddedOnce<OutboxAdded> services "The outbox can only be added once: by `UseOutboxWith` or `AddOutboxWith`."
        // Invalid settings fail now, not on the first command
        let options = OutboxConfiguration.Read configuration
        if not (isNull configure) then configure.Invoke options
        options.Validate()

        match typeof<'OutboxStore> with
        | t when t = typeof<RavenDB> ->
            RegisterRavenDocumentStore(services, configuration)
            services.AddSingleton<IOutbox>(fun sp -> RavenDBOutbox(sp.GetRequiredService<IDocumentStore>(), options) :> IOutbox) |> ignore
        | t when t = typeof<MongoDB> ->
            RegisterMongoDatabase(services, configuration)
            services.AddSingleton<IOutbox>(fun sp -> MongoDBOutbox(sp.GetRequiredService<IMongoDatabase>(), options) :> IOutbox) |> ignore
        | _ -> raise (Exception("Only `RavenDB` and `MongoDB` can be used as an Outbox store generic option!"))
        services

    /// The outbox in 'OutboxStore (RavenDB or MongoDB): once an aggregate is stored, event-sourced or state-based, its
    /// PublishedEvents are recorded there. Its settings come from the Outbox configuration section, and it can be added
    /// before or after the aggregates
    let AddOutboxWith<'OutboxStore
        when 'OutboxStore : not struct and 'OutboxStore : (new : unit -> 'OutboxStore) and 'OutboxStore :> StateBasedAggregateRepository>
        (services: IServiceCollection) (configuration: IConfiguration) =
        AddOutbox<'OutboxStore> services configuration null

[<AutoOpen>]
module HostBuilderExtensions =
    let UseAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> EventSourcedAggregateRepository>
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddAggregatesWithEventSourcing<'AggregateRepository> services ctx.Configuration aggregatesAssembly |> ignore)

    let UseAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddAggregatesWithoutEventSourcing<'AggregateRepository> services ctx.Configuration aggregatesAssembly |> ignore)

    let UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct and 'EventSourcedAggregateRepository : (new : unit -> 'EventSourcedAggregateRepository)
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct and 'TStateBasedAggregateRepository : (new : unit -> 'TStateBasedAggregateRepository)
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository> services ctx.Configuration aggregatesAssembly |> ignore)

    /// The outbox in 'OutboxStore (RavenDB or MongoDB), recording the PublishedEvents of every stored aggregate
    let UseOutboxWith<'OutboxStore
        when 'OutboxStore : not struct and 'OutboxStore : (new : unit -> 'OutboxStore) and 'OutboxStore :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder) =
        hostBuilder.ConfigureServices(fun ctx services ->
            AddOutboxWith<'OutboxStore> services ctx.Configuration |> ignore)

// C#-friendly (tupled) extension methods delegating to the F# functions above
[<Extension>]
module CSharp_HostBuilderExtensions =
    [<Extension>]
    let UseAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> EventSourcedAggregateRepository>
        (hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        HostBuilderExtensions.UseAggregatesWithEventSourcing<'AggregateRepository> hostBuilder aggregatesAssembly

    [<Extension>]
    let UseAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        HostBuilderExtensions.UseAggregatesWithoutEventSourcing<'AggregateRepository> hostBuilder aggregatesAssembly

    [<Extension>]
    let UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct and 'EventSourcedAggregateRepository : (new : unit -> 'EventSourcedAggregateRepository)
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct and 'TStateBasedAggregateRepository : (new : unit -> 'TStateBasedAggregateRepository)
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        HostBuilderExtensions.UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository> hostBuilder aggregatesAssembly

    /// The outbox in 'OutboxStore (RavenDB or MongoDB), with the Outbox configuration section's settings, which configure may change
    [<Extension>]
    let UseOutboxWith<'OutboxStore
        when 'OutboxStore : not struct and 'OutboxStore : (new : unit -> 'OutboxStore) and 'OutboxStore :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder, [<Optional; DefaultParameterValue(null: Action<OutboxOptions>)>] configure: Action<OutboxOptions>) =
        hostBuilder.ConfigureServices(fun ctx services ->
            ServiceCollectionExtensions.AddOutbox<'OutboxStore> services ctx.Configuration configure |> ignore)

/// Extension methods for IHostApplicationBuilder (Host.CreateApplicationBuilder, WebApplication.CreateBuilder)
/// and IServiceCollection, from C# or F#, delegating to the F# functions above
[<Extension; AbstractClass; Sealed>]
type AggregatesExtensions =
    [<Extension>]
    static member UseAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> EventSourcedAggregateRepository>
        (builder: IHostApplicationBuilder, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWithEventSourcing<'AggregateRepository>
            builder.Services builder.Configuration aggregatesAssembly |> ignore
        builder

    [<Extension>]
    static member UseAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> StateBasedAggregateRepository>
        (builder: IHostApplicationBuilder, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWithoutEventSourcing<'AggregateRepository>
            builder.Services builder.Configuration aggregatesAssembly |> ignore
        builder

    [<Extension>]
    static member UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct and 'EventSourcedAggregateRepository : (new : unit -> 'EventSourcedAggregateRepository)
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct and 'TStateBasedAggregateRepository : (new : unit -> 'TStateBasedAggregateRepository)
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (builder: IHostApplicationBuilder, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository>
            builder.Services builder.Configuration aggregatesAssembly |> ignore
        builder

    /// The outbox in 'OutboxStore (RavenDB or MongoDB), recording the PublishedEvents of every stored aggregate.
    /// Its settings come from the Outbox configuration section, and configure may change them
    [<Extension>]
    static member UseOutboxWith<'OutboxStore
        when 'OutboxStore : not struct and 'OutboxStore : (new : unit -> 'OutboxStore) and 'OutboxStore :> StateBasedAggregateRepository>
        (builder: IHostApplicationBuilder, [<Optional; DefaultParameterValue(null: Action<OutboxOptions>)>] configure: Action<OutboxOptions>) =
        ServiceCollectionExtensions.AddOutbox<'OutboxStore> builder.Services builder.Configuration configure |> ignore
        builder

    [<Extension>]
    static member AddAggregatesWithEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> EventSourcedAggregateRepository>
        (services: IServiceCollection, configuration: IConfiguration, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWithEventSourcing<'AggregateRepository> services configuration aggregatesAssembly

    [<Extension>]
    static member AddAggregatesWithoutEventSourcing<'AggregateRepository
        when 'AggregateRepository : not struct and 'AggregateRepository : (new : unit -> 'AggregateRepository) and 'AggregateRepository :> StateBasedAggregateRepository>
        (services: IServiceCollection, configuration: IConfiguration, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWithoutEventSourcing<'AggregateRepository> services configuration aggregatesAssembly

    [<Extension>]
    static member AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository
        when 'EventSourcedAggregateRepository : not struct and 'EventSourcedAggregateRepository : (new : unit -> 'EventSourcedAggregateRepository)
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct and 'TStateBasedAggregateRepository : (new : unit -> 'TStateBasedAggregateRepository)
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository>
        (services: IServiceCollection, configuration: IConfiguration, aggregatesAssembly: Assembly) =
        ServiceCollectionExtensions.AddAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository>
            services configuration aggregatesAssembly

    /// The outbox in 'OutboxStore (RavenDB or MongoDB), recording the PublishedEvents of every stored aggregate.
    /// Its settings come from the Outbox configuration section, and configure may change them
    [<Extension>]
    static member AddOutboxWith<'OutboxStore
        when 'OutboxStore : not struct and 'OutboxStore : (new : unit -> 'OutboxStore) and 'OutboxStore :> StateBasedAggregateRepository>
        (services: IServiceCollection, configuration: IConfiguration, [<Optional; DefaultParameterValue(null: Action<OutboxOptions>)>] configure: Action<OutboxOptions>) =
        ServiceCollectionExtensions.AddOutbox<'OutboxStore> services configuration configure
