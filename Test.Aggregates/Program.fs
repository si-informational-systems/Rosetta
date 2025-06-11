module Program

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Configuration
open System.IO
open SI.Rosetta.Aggregates.HostBuilder
open TestFSharp

[<EntryPoint>]
let main args =
    let host = 
        Host.CreateDefaultBuilder(args)
        |> fun builder -> 
            builder.ConfigureAppConfiguration(fun context config ->
                config
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("config/appsettings.json", optional = false)
                    .AddEnvironmentVariables("SI_")
                    .AddCommandLine(args)
                |> ignore)
        |> fun builder -> UseAggregatesWith<EventStore> builder typeof<PersonAggregateHandler>.Assembly
        |> fun builder -> builder.ConfigureServices(fun ctx services -> 
            services.AddHostedService<ServiceInstance>() |> ignore
        )
        |> fun builder -> builder.Build()

    host.Run()
    0