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

[<AutoOpen>]
module HostBuilderExtensionsCommon =
    let HostBuilderEventSourcedExtensionInUse : string = "SI.Rosetta.Aggregates.UseEventSourcedAggregates"
    let HostBuilderStateBasedExtensionInUse : string = "SI.Rosetta.Aggregates.UseStateBasedAggregates"

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

[<AutoOpen>]
module HostBuilderExtensions =
    let UseAggregatesWithEventSourcing<'AggregateRepository 
        when 'AggregateRepository : not struct and 'AggregateRepository :> EventSourcedAggregateRepository> 
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderEventSourcedExtensionInUse) then
                raise (InvalidOperationException("`UseAggregatesWithEventSourcing` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderEventSourcedExtensionInUse, null)
            
            match typeof<'AggregateRepository> with 
            | t when t = typeof<KurrentDB> ->
                let kurrentDBAggRep = InitializeKurrentDB (ctx.Configuration)
                services.AddSingleton(typeof<IEventSourcedAggregateRepository>, kurrentDBAggRep) |> ignore
                ()
            | _ -> raise (Exception("Only types extending `EventSourcedAggregateRepository` can be used as an Aggregate Repository generic option!"))

            RegisterAggregateHandlers(services, aggregatesAssembly)
        )

    let UseAggregatesWithoutEventSourcing<'AggregateRepository 
        when 'AggregateRepository : not struct and 'AggregateRepository :> StateBasedAggregateRepository> 
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderStateBasedExtensionInUse) then
                raise (InvalidOperationException("`UseAggregatesWithoutEventSourcing` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderStateBasedExtensionInUse, null)
            
            match typeof<'AggregateRepository> with 
            | t when t = typeof<RavenDB> ->
                if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                    services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(ctx.Configuration)) |> ignore
                services.AddSingleton<IStateBasedAggregateRepository, RavenDBAggregateRepository>() |> ignore
            | t when t = typeof<MongoDB> ->
                RegisterMongoDatabase(services, ctx.Configuration)
                services.AddSingleton<IStateBasedAggregateRepository, MongoDBAggregateRepository>() |> ignore
            | _ -> raise (Exception("Only types extending `StateBasedAggregateRepository` can be used as an Aggregate Repository generic option!"))

            RegisterAggregateHandlers(services, aggregatesAssembly)
        )

    let UseAggregatesWith<'EventSourcedAggregateRepository, 'TStateBasedAggregateRepository 
        when 'EventSourcedAggregateRepository : not struct 
        and 'EventSourcedAggregateRepository :> EventSourcedAggregateRepository
        and 'TStateBasedAggregateRepository : not struct 
        and 'TStateBasedAggregateRepository :> StateBasedAggregateRepository> 
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        UseAggregatesWithEventSourcing<'EventSourcedAggregateRepository> hostBuilder aggregatesAssembly |> ignore
        UseAggregatesWithoutEventSourcing<'TStateBasedAggregateRepository> hostBuilder aggregatesAssembly |> ignore
        hostBuilder

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
        UseAggregatesWithEventSourcing<'EventSourcedAggregateRepository>(hostBuilder, aggregatesAssembly) |> ignore
        UseAggregatesWithoutEventSourcing<'TStateBasedAggregateRepository>(hostBuilder, aggregatesAssembly) |> ignore
        hostBuilder