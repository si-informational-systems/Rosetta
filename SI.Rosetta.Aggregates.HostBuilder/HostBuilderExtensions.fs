namespace SI.Rosetta.Aggregates.HostBuilder

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open System
open System.Reflection
open System.Runtime.CompilerServices
open EventStore.Client
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.EventStore
open SI.Stack.RavenDB
open SI.Rosetta.Aggregates.RavenDB
open Raven.Client.Documents

[<AutoOpen>]
module HostBuilderExtensionsCommon =
    let HostBuilderEventSourcedExtensionInUse : string = "SI.Rosetta.Aggregates.UseEventSourcedAggregates"
    let HostBuilderStateBasedExtensionInUse : string = "SI.Rosetta.Aggregates.UseStateBasedAggregates"

[<AutoOpen>]
module HostBuilderEventStoreExtensionCommmon =

    let private CheckEventStoreAvailable(client: EventStoreClient) =
        (client.GetStreamMetadataAsync("$ce-Any")).Result |> ignore

    let InitializeEventStore (config: IConfiguration) =
        let esAggRep = 
            let settings = EventStoreClientSettings.Create(config.["EventStoreDB:ConnectionString"])
            let client = new EventStoreClient(settings)
            CheckEventStoreAvailable client
            new EventStoreAggregateRepository(client)
        esAggRep

[<AutoOpen>]
module HostBuilderRavenDBExtensionCommmon =
    // Custom contract resolver to exclude Version field from serialization
    type AggregateStateContractResolver() =
        inherit Newtonsoft.Json.Serialization.DefaultContractResolver()
    
        override __.CreateProperty(memberInfo: Reflection.MemberInfo, memberSerialization: Newtonsoft.Json.MemberSerialization) =
            let property = base.CreateProperty(memberInfo, memberSerialization)
        
            // Exclude Version field from serialization
            if property.PropertyName = "Version" then
                property.ShouldSerialize <- fun _ -> false
                property.ShouldDeserialize <- fun _ -> false
        
            property

    let CreateRavenDocumentStore(config: IConfiguration) = 
        let docStore = 
            RavenDocumentStoreFactory.CreateAndInitializeDocumentStore(
                RavenConfig.FromConfiguration(config),
                fun conventions -> 
                    // Configure serialization to exclude Version field
                    conventions.Serialization <- 
                        let serialization = Raven.Client.Json.Serialization.NewtonsoftJson.NewtonsoftJsonSerializationConventions()
                        serialization.CustomizeJsonSerializer <- fun serializer ->
                            serializer.ContractResolver <- new AggregateStateContractResolver()
                        serialization
            );
        docStore;

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
            | t when t = typeof<EventStore> ->
                let esAggRep = InitializeEventStore (ctx.Configuration)
                services.AddSingleton(typeof<IEventSourcedAggregateRepository>, esAggRep) |> ignore
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

[<Extension>]
module CSharp_HostBuilderExtensions =
    [<Extension>]
    let UseAggregatesWithEventSourcing<'AggregateRepository 
        when 'AggregateRepository : not struct and 'AggregateRepository :> EventSourcedAggregateRepository>
        (hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderEventSourcedExtensionInUse) then
                raise (InvalidOperationException("`UseAggregatesWithEventSourcing` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderEventSourcedExtensionInUse, null)
            
            match typeof<'AggregateRepository> with 
            | t when t = typeof<EventStore> ->
                let esAggRep = InitializeEventStore (ctx.Configuration)
                services.AddSingleton(typeof<IEventSourcedAggregateRepository>, esAggRep) |> ignore

            | _ -> raise (Exception("Only types extending `EventSourcedAggregateRepository` can be used as an Aggregate Repository generic option!"))

            RegisterAggregateHandlers(services, aggregatesAssembly)
        )

    [<Extension>]
    let UseAggregatesWithoutEventSourcing<'AggregateRepository 
        when 'AggregateRepository : not struct and 'AggregateRepository :> StateBasedAggregateRepository>
        (hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderStateBasedExtensionInUse) then
                raise (InvalidOperationException("`UseAggregatesWithoutEventSourcing` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderStateBasedExtensionInUse, null)
            
            match typeof<'AggregateRepository> with 
            | t when t = typeof<RavenDB> ->
                if not (services |> Seq.exists(fun descriptor -> descriptor.ServiceType = typeof<IDocumentStore>)) then
                    services.AddSingleton<IDocumentStore>(CreateRavenDocumentStore(ctx.Configuration)) |> ignore
                services.AddSingleton<IStateBasedAggregateRepository, RavenDBAggregateRepository>() |> ignore

            | _ -> raise (Exception("Only types extending `StateBasedAggregateRepository` can be used as an Aggregate Repository generic option!"))

            RegisterAggregateHandlers(services, aggregatesAssembly)
        )

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