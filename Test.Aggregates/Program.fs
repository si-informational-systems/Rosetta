module Program

open System
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open SI.Rosetta.Aggregates.HostBuilder
open TestFSharp

[<EntryPoint>]
let main args =
    // config/ and wwwroot/ are copied to the output, so they're found whatever the working directory is
    let builder = WebApplication.CreateBuilder(WebApplicationOptions(Args = args, ContentRootPath = AppContext.BaseDirectory))
    builder.Configuration
        .AddJsonFile("config/appsettings.json", optional = false)
        .AddCommandLine(args)
    |> ignore

    UseAggregatesWith<KurrentDB, RavenDB> builder.Host typeof<PersonAggregateHandler>.Assembly |> ignore
    // UseAggregatesWith is used when a combination of both EventSourced and NonEventSourced persistence is used
    // UseAggregatesWithEventSourcing is used when only EventSourced persistence is used
    // UseAggregatesWithoutEventSourcing is used when only NonEventSourced persistence is used
    builder.Services.AddSingleton<RavenDocuments>() |> ignore

    let app = builder.Build()
    // The test page shows why a command failed
    app.UseDeveloperExceptionPage() |> ignore
    app.UseDefaultFiles().UseStaticFiles() |> ignore
    PersonEndpoints.Map app
    OrganizationEndpoints.Map app
    QueryEndpoints.Map app

    app.Run()
    0
