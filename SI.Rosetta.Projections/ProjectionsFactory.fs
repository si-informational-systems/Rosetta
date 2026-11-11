namespace SI.Rosetta.Projections

open System
open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open SI.Rosetta.Common

type ProjectionInfo = {
    Name: string
    SubscriptionStreamName: string
}

/// The projection types: the ones that inherit Projection<'TEvent>, as IProjectionInstance<'TEvent> marks them
module ProjectionTypes =
    let private ProjectionInstanceOf (t: Type) =
        t.GetInterfaces()
        |> Array.tryFind (fun i -> i.IsGenericType && i.GetGenericTypeDefinition() = typedefof<IProjectionInstance<_>>)

    /// Whether the projection runs: it doesn't when it has [<DisabledProjection>]
    let IsActive (t: Type) =
        isNull (t.GetCustomAttribute(typeof<DisabledProjection>))

    /// The events DU the projection type projects
    let EventsTypeOf (t: Type) =
        match ProjectionInstanceOf t with
        | Some instance -> instance.GetGenericArguments().[0]
        | None -> raise (ArgumentException(sprintf "%s isn't a projection: it doesn't inherit Projection<'TEvent>." t.Name))

    /// The active projection types in the assembly
    let In (assembly: Assembly) =
        assembly.GetTypes()
        |> Array.filter (fun t -> (ProjectionInstanceOf t).IsSome && IsActive t)

type ProjectionsFactory(provider: IServiceProvider) =
    let CheckpointsPrefix = "Checkpoints"
    let ProjectionPrefix = "Projection"
    
    member private this.GetProjectionName(t: Type) =
        t.Name.Replace(ProjectionPrefix, String.Empty)
        
    member private this.GetSubscriptionStreamName(t: Type) =
        try
            let attrInfo = t.GetCustomAttribute(typeof<HandlesStream>) :?> HandlesStream
            attrInfo.Name
        with
        | _ ->
            raise (InvalidOperationException(sprintf "Type %s must have HandlesStream attribute" t.Name))
        
    member private this.GetProjectionInfo(t: Type) : ProjectionInfo =
        { Name = this.GetProjectionName(t)
          SubscriptionStreamName = this.GetSubscriptionStreamName(t) }
        
    member this.SetNameAndStreamName(t: Type) (proj: Projection<_>) =
        let pi = this.GetProjectionInfo(t)
        proj.Name <- pi.Name
        proj.SubscriptionStreamName <- pi.SubscriptionStreamName
        
    /// The store's service: the one registered under its kind (ProjectionStoreKinds) when there are two stores, the
    /// one registered on its own otherwise
    member private this.OfStore<'T when 'T : not struct>(kind: string) : 'T =
        match box (provider.GetKeyedService<'T>(kind)) with
        | null -> provider.GetService<'T>()
        | service -> unbox<'T> service

    member this.LoadCheckpoint(proj: Projection<_>) =
        task {
            let! checkpoint = proj.CheckpointStore.ReadAsync(sprintf "%s-%s" CheckpointsPrefix proj.Name).ConfigureAwait(false)
            proj.Checkpoint <- checkpoint
        }

    /// A projection whose [<ProjectionVersion>] went up is rebuilt: its read models' tables or collections (IProjectInto)
    /// are emptied, then its checkpoint starts again from 0 with the new version. In that order, so a crash between them
    /// clears again
    member this.RebuildIfVersioned(t: Type, proj: Projection<_>) =
        task {
            let version = ProjectionsFactory.VersionOf t
            let stored = proj.Checkpoint.Version
            if stored > version then
                raise (InvalidOperationException(
                    sprintf "Projection %s is at version %d, but its read models were built by version %d: a newer version of it runs elsewhere." proj.Name version stored))
            if stored < version then
                do! this.Rebuild(t, proj, version, sprintf "its [<ProjectionVersion>] went from %d to %d" stored version)
        }

    static member private VersionOf(t: Type) =
        match t.GetCustomAttribute(typeof<ProjectionVersion>) with
        | :? ProjectionVersion as attribute -> attribute.Version
        | _ -> 0

    /// Empties the projection's read models (IProjectInto), then starts its checkpoint again from 0 at version. In that
    /// order, so a crash between them clears again. One with nothing to empty doesn't start, unless it's marked
    /// [<RebuildWithoutClearing>]: replaying over its read models would apply every event twice
    member private this.Rebuild(t: Type, proj: Projection<_>, version: int, reason: string) : Task =
        task {
            ProjectionReadModels.RequireClearable t reason
            let clear = ProjectionReadModels.Of t
            if clear.Length > 0 then
                if isNull (box proj.UnitOfWork) then
                    raise (InvalidOperationException(sprintf "Projection %s can't clear its read models: its store has no unit of work." proj.Name))
                do! proj.UnitOfWork.ClearAsync(clear).ConfigureAwait(false)
            let reset = Checkpoint(Id = proj.Checkpoint.Id, Value = 0UL, Version = version)
            do! proj.CheckpointStore.StoreAsync(reset).ConfigureAwait(false)
            proj.Checkpoint <- reset
        } :> Task

    /// A projection whose stream was replaced under it (a custom projection's cp- stream linked again, or renamed) is
    /// rebuilt: its read models were built from links the stream no longer holds
    member this.RebuildIfReplaced(t: Type, proj: Projection<_>, subscription: obj) =
        task {
            match subscription with
            | :? ISubscriptionHistory as history ->
                match! history.ReplacedSinceAsync(proj.Checkpoint.Value).ConfigureAwait(false) with
                | Some reason ->
                    match provider.GetService<ILogger<ProjectionsFactory>>() with
                    | null -> ()
                    | logger ->
                        logger.LogWarning("Projection {Name} is REBUILT: {Reason}. Its read models (IProjectInto) are emptied, and it starts again from the start of the stream.",
                            proj.Name, reason)
                    do! this.Rebuild(t, proj, ProjectionsFactory.VersionOf t, reason)
                | None -> ()
            | _ -> ()
        }
        
    member this.CreateSubscription<'TEvent when 'TEvent :> IAggregateEvents>(proj: Projection<'TEvent>) =
        let subscription = provider.GetRequiredService<ISubscriptionFactory>().Create<'TEvent>()
        subscription.Name <- proj.Name
        subscription.StreamName <- proj.SubscriptionStreamName
        subscription.EventReceived <- proj.ProjectAsync
        subscription.SkipUnknownEvents <- proj.SkipUnknownEvents
        subscription

    member this.CreateHandlers<'TEvent when 'TEvent :> IAggregateEvents>(t: Type) =
        let handlerTypes = 
            t.GetInterfaces()
            |> Seq.filter (fun iType -> 
                iType.IsGenericType && 
                iType.GetGenericTypeDefinition() = typedefof<IAmHandledBy<_, _>>)
            |> Seq.map (fun iType -> iType.GetGenericArguments().[0])
            |> Seq.toArray
            
        let handlers = List<IProjectionHandler<'TEvent>>()

        let projectionHandlerFactory = provider.GetRequiredService<IProjectionHandlerFactory>()
        for handlerType in handlerTypes do
            let handler = projectionHandlerFactory.Create(handlerType)
            handlers.Add(handler)
            
        handlers
        
    member this.IsActive(t: Type) = ProjectionTypes.IsActive t

    member this.SkipsUnknownEvents(t: Type) =
        not (isNull (t.GetCustomAttribute(typeof<SkipUnknownEvents>)))

    /// Every step typed: the projection of 'TEvent that projectionType declares
    member private this.CreateOfAsync<'TEvent when 'TEvent :> IAggregateEvents>(projectionType: Type) : Task<IProjection> =
        task {
            if not (typeof<IProjectionInstance<'TEvent>>.IsAssignableFrom projectionType) then
                raise (ArgumentException(sprintf "%s isn't a projection of %s: it doesn't inherit Projection<%s>." projectionType.Name typeof<'TEvent>.Name typeof<'TEvent>.Name))
            let projection = Projection<'TEvent>()
            this.SetNameAndStreamName projectionType projection
            projection.SkipUnknownEvents <- this.SkipsUnknownEvents projectionType
            // The store its handlers write to: its transactions and its checkpoints
            let kind = ProjectionStoreKinds.Of projectionType
            projection.CheckpointStore <- this.OfStore<ICheckpointStore> kind
            if isNull (box projection.CheckpointStore) then
                raise (InvalidOperationException(sprintf "Projection %s has no checkpoint store: register a projections store." projection.Name))
            // Every Rosetta store registers one. Without it, the handlers' writes and the checkpoint aren't atomic
            projection.UnitOfWork <- this.OfStore<IProjectionsUnitOfWork> kind
            if not (isNull (box projection.UnitOfWork)) then
                do! projection.UnitOfWork.PrepareAsync(ProjectionReadModels.Of projectionType).ConfigureAwait(false)
            do! this.LoadCheckpoint(projection).ConfigureAwait(false)
            do! this.RebuildIfVersioned(projectionType, projection).ConfigureAwait(false)
            projection.Subscription <- this.CreateSubscription projection
            do! this.RebuildIfReplaced(projectionType, projection, box projection.Subscription).ConfigureAwait(false)
            projection.Handlers <- this.CreateHandlers<'TEvent> projectionType
            return projection :> IProjection
        }

    interface IProjectionsFactory with
        member this.CreateFromAssemblyAsync(projectionsAssembly: Assembly) =
            task {
                let ret = List<IProjection>()
                for t in ProjectionTypes.In projectionsAssembly do
                    let! proj = (this :> IProjectionsFactory).CreateAsync(t).ConfigureAwait(false)
                    ret.Add(proj)
                return ret :> IList<IProjection>
            }

        member this.CreateAsync(t: Type) =
            // The one step that needs reflection: the events DU is only known from t. nameof, so a rename can't break it
            typeof<ProjectionsFactory>.GetMethod(nameof this.CreateOfAsync, BindingFlags.Instance ||| BindingFlags.NonPublic)
                .MakeGenericMethod(ProjectionTypes.EventsTypeOf t)
                .Invoke(this, [| t |]) :?> Task<IProjection>

        member this.CreateAsync<'TEvent when 'TEvent :> IAggregateEvents>(projectionType: Type) =
            this.CreateOfAsync<'TEvent> projectionType
