module Program

open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Configuration
open System
open System.Reflection
open SI.Rosetta.Projections.HostBuilder
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
                    .AddEnvironmentVariables("SI_")
                    .AddCommandLine(args)
                |> ignore)
        |> fun builder -> UseProjectionsWith<KurrentDB, RavenDB> builder (Assembly.GetAssembly(typeof<PersonProjection>))
        |> fun builder -> builder.Build()

    host.Run()
    // 1 when the Projections failed: Rosetta stops the host and sets Environment.ExitCode
    Environment.ExitCode