module Program

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open System
open SI.Rosetta.Aggregates.HostBuilder
open TestFSharp

[<EntryPoint>]
let main args =
    let host = 
        Host.CreateDefaultBuilder(args)
        |> fun builder -> 
            builder.ConfigureAppConfiguration(fun context config ->
                config
                    // config/ is copied to the output, so it's found whatever the working directory is
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("config/appsettings.json", optional = false)
                    .AddCommandLine(args)
                |> ignore)
        |> fun builder -> UseAggregatesWith<KurrentDB, RavenDB> builder typeof<PersonAggregateHandler>.Assembly
        // UseAggregatesWith is used when a combination of both EventSourced and NonEventSourced persistence is used
        // UseAggregatesWithEventSourcing is used when only EventSourced persistence is used
        // UseAggregatesWithoutEventSourcing is used when only NonEventSourced persistence is used
        |> fun builder -> builder.ConfigureServices(fun ctx services ->
            services.AddHostedService<ServiceInstance>() |> ignore
        )
        |> fun builder -> builder.Build()

    host.Run()
    0