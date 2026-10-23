namespace SI.Rosetta.Projections

open System
open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
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
        
    member this.LoadCheckpoint(proj: Projection<_>) =
        task {
            let cr = provider.GetRequiredService<ICheckpointStore>()
            let! checkpoint = cr.ReadAsync(sprintf "%s-%s" CheckpointsPrefix proj.Name).ConfigureAwait(false)
            proj.Checkpoint <- checkpoint
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
            do! this.LoadCheckpoint(projection).ConfigureAwait(false)
            projection.CheckpointStore <- provider.GetRequiredService<ICheckpointStore>()
            projection.Subscription <- this.CreateSubscription projection
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
            // The one step that needs reflection: the events DU is only known from t
            typeof<ProjectionsFactory>.GetMethod("CreateOfAsync", BindingFlags.Instance ||| BindingFlags.NonPublic)
                .MakeGenericMethod(ProjectionTypes.EventsTypeOf t)
                .Invoke(this, [| t |]) :?> Task<IProjection>

        member this.CreateAsync<'TEvent when 'TEvent :> IAggregateEvents>(projectionType: Type) =
            this.CreateOfAsync<'TEvent> projectionType
