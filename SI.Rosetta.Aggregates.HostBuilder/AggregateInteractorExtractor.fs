namespace SI.Rosetta.Aggregates.HostBuilder

open Microsoft.Extensions.DependencyInjection
open System.Reflection
open SI.Rosetta.Aggregates

[<AutoOpen>]
module AggregateHandlerExtractor =
    let RegisterAggregateHandlers (services: IServiceCollection, assembly: Assembly) =
        assembly.GetTypes()
            |> Seq.filter (fun t -> 
                typeof<IAggregateHandler>.IsAssignableFrom(t) && t.IsClass)
            |> Seq.iter (fun t ->
                let iinteractor = 
                    t.GetInterfaces()
                    |> Array.filter (fun x -> x.Name <> typeof<IAggregateHandler>.Name)
                    |> Array.head
                services.AddTransient(iinteractor, t) |> ignore)
