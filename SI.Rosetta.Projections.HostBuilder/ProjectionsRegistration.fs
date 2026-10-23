namespace SI.Rosetta.Projections.HostBuilder

open System
open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open SI.Rosetta.Common
open SI.Rosetta.Projections

[<AutoOpen>]
module ProjectionsExtractor =
    let private IsProjectionHandler (t: Type) =
        t.GetInterfaces()
        |> Array.exists (fun i -> i.IsGenericType && i.GetGenericTypeDefinition() = typedefof<IProjectionHandler<_>>)

    /// The projection handlers in the assembly
    let internal HandlerTypesIn (assembly: Assembly) =
        assembly.GetTypes() |> Array.filter IsProjectionHandler

    /// The handlers the projection type declares with IAmHandledBy
    let internal DeclaredHandlerTypesOf (projectionType: Type) =
        projectionType.GetInterfaces()
        |> Array.filter (fun i -> i.IsGenericType && i.GetGenericTypeDefinition() = typedefof<IAmHandledBy<_, _>>)
        |> Array.map (fun i -> i.GetGenericArguments().[0])

    let RegisterProjectionHandlers (services: IServiceCollection, assembly: Assembly) =
        HandlerTypesIn assembly |> Array.iter (fun t -> services.AddTransient(t) |> ignore)

/// A projection named by its type, which is created with every step typed
type internal NamedProjection =
    { ProjectionType: Type
      EventsType: Type
      Create: IProjectionsFactory -> Task<IProjection> }

/// The projections to run: named one by one, found in assemblies, or both. Each runs once. A named projection's handlers
/// are the ones it declares with IAmHandledBy; an assembly's are all the projection handlers in it. Every handler is
/// registered once, as a transient service of its own type
[<Sealed>]
type ProjectionsRegistration internal () =
    let Named = List<NamedProjection>()
    let Scanned = List<Assembly>()

    /// The projection, of the events DU 'TEvent
    member this.Projection<'TProjection, 'TEvent
        when 'TProjection :> IProjectionInstance<'TEvent> and 'TEvent :> IAggregateEvents>() =
        if not (Named |> Seq.exists (fun named -> named.ProjectionType = typeof<'TProjection>)) then
            Named.Add
                { ProjectionType = typeof<'TProjection>
                  EventsType = typeof<'TEvent>
                  Create = fun factory -> factory.CreateAsync<'TEvent> typeof<'TProjection> }

    /// Every active projection in the assembly (one without [<DisabledProjection>]), and every ICustomProjectionEvents DU's
    /// custom projection
    member this.FromAssembly(assembly: Assembly) =
        if isNull assembly then nullArg (nameof assembly)
        if not (Scanned.Contains assembly) then
            Scanned.Add assembly

    /// Every active projection in the assembly that holds 'T, and its custom projections
    member this.FromAssemblyOf<'T>() =
        this.FromAssembly typeof<'T>.Assembly

    member internal this.Assemblies = Scanned :> IReadOnlyList<Assembly>
    member internal this.NamedProjections = Named :> IReadOnlyList<NamedProjection>

    member private this.NamedOf(t: Type) =
        Named |> Seq.tryFind (fun named -> named.ProjectionType = t)

    /// Every projection handler, each once: the ones in the assemblies, and the ones the named projections declare
    member this.HandlerTypes : IReadOnlyList<Type> =
        Seq.append
            (Scanned |> Seq.collect HandlerTypesIn)
            (Named |> Seq.collect (fun named -> DeclaredHandlerTypesOf named.ProjectionType))
        |> Seq.distinct
        |> Seq.toArray
        :> IReadOnlyList<Type>

    /// The projections that run, each once: the named ones, then the ones in the assemblies that weren't named.
    /// A projection with [<DisabledProjection>] doesn't, even when it's named
    member this.ProjectionTypes : IReadOnlyList<Type> =
        Seq.append
            (Named |> Seq.map (fun named -> named.ProjectionType))
            (Scanned |> Seq.collect ProjectionTypes.In)
        |> Seq.filter ProjectionTypes.IsActive
        |> Seq.distinct
        |> Seq.toArray
        :> IReadOnlyList<Type>

    /// Creates the projections that run: the named ones with every step typed
    member internal this.CreateProjectionsAsync(factory: IProjectionsFactory) =
        task {
            let projections = List<IProjection>()
            for t in this.ProjectionTypes do
                let! projection =
                    match this.NamedOf t with
                    | Some named -> named.Create factory
                    | None -> factory.CreateAsync t
                projections.Add projection
            return projections :> IReadOnlyList<IProjection>
        }

    /// The projections register adds
    static member internal Of(register: Action<ProjectionsRegistration>) =
        if isNull register then nullArg (nameof register)
        let registration = ProjectionsRegistration()
        register.Invoke registration
        registration

    static member internal OfAssembly(assembly: Assembly) =
        ProjectionsRegistration.Of(fun registration -> registration.FromAssembly assembly)
