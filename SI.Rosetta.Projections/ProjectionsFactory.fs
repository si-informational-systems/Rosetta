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

type ProjectionsFactory(provider: IServiceProvider) =
    let CheckpointsPrefix = "Checkpoints"
    let ProjectionPrefix = "Projection"
    
    member private this.GetProjectionName(t: Type) =
        t.Name.Replace(ProjectionPrefix, String.Empty)
        
    member private this.GetSubscriptionStreamName(t: Type) =
        let attrInfo = t.GetCustomAttribute(typeof<HandlesStream>) :?> HandlesStream
        attrInfo.Name
        
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
        
    member this.CreateSubscription<'TEvent when 'TEvent :> IEvents>(proj: Projection<'TEvent>) =
        let subscription = provider.GetRequiredService<ISubscriptionFactory>().Create<'TEvent>()
        subscription.Name <- proj.Name
        subscription.StreamName <- proj.SubscriptionStreamName
        subscription.EventReceived <- proj.ProjectAsync
        subscription
        
    member this.CreateHandlers<'TEvent when 'TEvent :> IEvents>(t: Type) =
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
        
    member this.IsActive(t: Type) =
        let attrInfo = t.GetCustomAttribute(typeof<DisabledProjection>)
        attrInfo = null
        
    interface IProjectionsFactory with
        member this.CreateFromAssemblyAsync(projectionsAssembly: Assembly) =
            task {
                let projectionTypes = 
                    projectionsAssembly.GetTypes()
                    |> Seq.filter (fun t -> 
                        t.GetInterfaces()
                        |> Array.exists (fun i -> 
                            i.IsGenericType && 
                            i.GetGenericTypeDefinition() = typedefof<IProjectionInstance<_>>))
                    |> Seq.filter this.IsActive
                    |> Seq.toList
                    
                let ret = List<IProjection>()
                
                for t in projectionTypes do
                    let! proj = (this :> IProjectionsFactory).CreateAsync(t).ConfigureAwait(false)
                    ret.Add(proj)
                    
                return ret
            }
            
        member this.CreateAsync(t: Type) =
            task {
                // Get the event type from the IProjection<T> interface
                let eventsType = 
                    t.GetInterfaces()
                    |> Seq.find (fun i -> 
                        i.IsGenericType && 
                        i.GetGenericTypeDefinition() = typedefof<IProjectionInstance<_>>)
                    |> fun i -> i.GetGenericArguments().[0]
                
                // Create the concrete Projection<T> type
                let projTypeDef = typedefof<Projection<_>>
                let constructedProj = projTypeDef.MakeGenericType [|eventsType|]
                let projection = Activator.CreateInstance(constructedProj)
                let projectionType = projection.GetType()

                // Set name and stream name
                let setNameAndStreamNameMethod =
                    this.GetType().GetMethod("SetNameAndStreamName")
                        .MakeGenericMethod(eventsType)
                setNameAndStreamNameMethod.Invoke(this, [|t; projection|]) |> ignore
                
                let loadCheckpointMethod = 
                    this.GetType().GetMethod("LoadCheckpoint")
                        .MakeGenericMethod(eventsType)
                do! (loadCheckpointMethod.Invoke(this, [|projection|]) :?> Task).ConfigureAwait(false)
                
                // Set checkpoint writer
                let checkpointStoreProp = projectionType.GetProperty("CheckpointStore")
                checkpointStoreProp.SetValue(projection, provider.GetRequiredService<ICheckpointStore>())
                
                // Create and set subscription
                let createSubscriptionMethod = 
                    this.GetType().GetMethod("CreateSubscription")
                        .MakeGenericMethod(eventsType)
                let subscription = createSubscriptionMethod.Invoke(this, [|projection|])
                let subscriptionProp = projectionType.GetProperty("Subscription")
                subscriptionProp.SetValue(projection, subscription)
                
                // Set handlers
                let createHandlersMethod =
                    this.GetType().GetMethod("CreateHandlers")
                        .MakeGenericMethod(eventsType)
                let handlers = createHandlersMethod.Invoke(this, [|t|])
                let handlersProp = projectionType.GetProperty("Handlers")
                handlersProp.SetValue(projection, handlers)
                
                return projection :?> IProjection
            }