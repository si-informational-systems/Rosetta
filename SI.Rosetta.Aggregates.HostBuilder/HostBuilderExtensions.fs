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

[<AutoOpen>]
module HostBuilderExtensionCommmon =
    let HostBuilderExtensionInUse : string = "SI.Rosetta.Aggregates.UseAggregates"

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
module HostBuilderExtensions =
    let UseAggregatesWith<'AggregateRepository when 'AggregateRepository : not struct and 'AggregateRepository :> EventStore> 
        (hostBuilder: IHostBuilder) (aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderExtensionInUse) then
                raise (InvalidOperationException("`UseAggregates` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderExtensionInUse, null)
            
            let esAggRep = InitializeEventStore (ctx.Configuration)
            services.AddSingleton(typeof<IAggregateRepository>, esAggRep) |> ignore

            RegisterAggregateHandlers(services, aggregatesAssembly)
        )

[<Extension>]
module CSharp_HostBuilderExtensions =
    [<Extension>]
    let UseAggregatesWith<'T when 'T : not struct and 'T :> EventStore>(hostBuilder: IHostBuilder, aggregatesAssembly: Assembly) =
        hostBuilder.ConfigureServices(fun ctx services ->
            if ctx.Properties.ContainsKey(HostBuilderExtensionInUse) then
                raise (InvalidOperationException("`UseAggregates` can only be used once!"))
                
            ctx.Properties.Add(HostBuilderExtensionInUse, null)
            
            let esAggRep = InitializeEventStore (ctx.Configuration)
            services.AddSingleton(typeof<IAggregateRepository>, esAggRep) |> ignore

            RegisterAggregateHandlers(services, aggregatesAssembly)
        )
