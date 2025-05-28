module Program

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Configuration
open System.IO
open SI.Rosetta.Projections.HostBuilder
open TestFSharp
open System.Reflection

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
        |> fun builder -> UseProjectionsWith<EventStore, Raven> builder (Assembly.GetAssembly(typeof<PersonProjection>))
        |> fun builder -> builder.Build()

    host.Run()
    0