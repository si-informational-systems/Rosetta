namespace SI.Rosetta.Aggregates.HostBuilder

open System
open System.Collections.Generic
open System.Reflection
open Microsoft.Extensions.DependencyInjection
open SI.Rosetta.Aggregates

[<AutoOpen>]
module AggregateHandlerExtractor =
    /// The interface a handler is registered under: its first one that isn't IAggregateHandler, i.e. its marker interface
    let internal MarkerInterfaceOf (handlerType: Type) =
        handlerType.GetInterfaces()
        |> Array.tryFind (fun i -> i.Name <> typeof<IAggregateHandler>.Name)
        |> Option.defaultWith (fun () ->
            raise (InvalidOperationException(
                sprintf "%s can't be registered: it needs a marker interface that inherits IAggregateHandler (e.g. I%s), which it's registered under and resolved by."
                    handlerType.Name handlerType.Name)))

    /// The aggregate handlers in the assembly
    let internal HandlerTypesIn (assembly: Assembly) =
        assembly.GetTypes()
        |> Array.filter (fun t -> typeof<IAggregateHandler>.IsAssignableFrom(t) && t.IsClass)

    let internal RegisterAggregateHandler (services: IServiceCollection) (handlerType: Type) =
        services.AddTransient(MarkerInterfaceOf handlerType, handlerType) |> ignore

    let RegisterAggregateHandlers (services: IServiceCollection, assembly: Assembly) =
        HandlerTypesIn assembly |> Array.iter (RegisterAggregateHandler services)

/// The aggregate handlers to register: named one by one, found in assemblies, or both. Each is registered once, as a
/// transient service under its marker interface (its first interface that isn't IAggregateHandler)
[<Sealed>]
type AggregatesRegistration internal () =
    let Handlers = List<Type>()

    let Add (handlerType: Type) =
        if not (Handlers.Contains handlerType) then
            Handlers.Add handlerType

    /// The handler, under its marker interface
    member this.Handler<'THandler when 'THandler :> IAggregateHandler and 'THandler : not struct>() =
        MarkerInterfaceOf typeof<'THandler> |> ignore
        Add typeof<'THandler>

    /// Every aggregate handler in the assembly
    member this.FromAssembly(assembly: Assembly) =
        if isNull assembly then nullArg (nameof assembly)
        HandlerTypesIn assembly |> Array.iter Add

    /// Every aggregate handler in the assembly that holds 'T
    member this.FromAssemblyOf<'T>() =
        this.FromAssembly typeof<'T>.Assembly

    /// The handlers to register, each once
    member this.HandlerTypes = Handlers :> IReadOnlyList<Type>

    /// The handlers register adds
    static member internal Of(register: Action<AggregatesRegistration>) =
        if isNull register then nullArg (nameof register)
        let registration = AggregatesRegistration()
        register.Invoke registration
        registration

    static member internal OfAssembly(assembly: Assembly) =
        AggregatesRegistration.Of(fun registration -> registration.FromAssembly assembly)
