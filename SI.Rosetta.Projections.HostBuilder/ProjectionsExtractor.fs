namespace SI.Rosetta.Projections.HostBuilder

open Microsoft.Extensions.DependencyInjection
open System.Reflection
open SI.Rosetta.Projections

[<AutoOpen>]
module ProjectionsExtractor =
    let RegisterProjectionHandlers (services: IServiceCollection, assembly: Assembly) =
        assembly.GetTypes()
            |> Seq.filter (fun t -> 
                t.GetInterfaces()
                |> Array.exists (fun i -> 
                    i.IsGenericType && 
                    i.GetGenericTypeDefinition() = typedefof<IProjectionHandler<_>>))
            |> Seq.iter (fun t -> services.AddTransient(t) |> ignore)

