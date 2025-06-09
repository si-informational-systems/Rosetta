namespace SI.Rosetta.Projections.TestKit

open SI.Rosetta.Projections
open SI.Rosetta.TestKit
open System
open Microsoft.Extensions.DependencyInjection
open Xunit.Sdk
open SI.Rosetta.Common

[<AbstractClass>]
type TestKitBase<'TProjection, 'TProjectionHandler, 'TEvent 
    when 'TEvent :> IEvents
    and 'TProjectionHandler :> IProjectionHandler<'TEvent>>() as this =
    let mutable TestValid = false
    let mutable DocumentId = Unchecked.defaultof<obj>
    let mutable projectionsFactory = Unchecked.defaultof<IProjectionsFactory>
    let mutable projectionsStore = Unchecked.defaultof<IProjectionsStore>
    let serviceCollection = ServiceCollection()

    let lazyServices = lazy (
        let handlerType = typeof<'TProjectionHandler>
        serviceCollection.AddTransient(handlerType) |> ignore
        serviceCollection.AddTransient<ICheckpointStore, StubCheckpointStore>() |> ignore
        serviceCollection.AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>() |> ignore
        serviceCollection.AddTransient<ISubscriptionFactory, TestKitSubscriptionFactory>() |> ignore
        serviceCollection.AddTransient<IProjectionsFactory, ProjectionsFactory>() |> ignore

        let store = TestKitProjectionStore()
        serviceCollection.AddSingleton<IProjectionsStore>(store) |> ignore
        serviceCollection.AddSingleton<INoSqlStore>(store) |> ignore
        serviceCollection.AddSingleton<ISqlStore>(store) |> ignore

        // Allow derived classes to configure additional services
        this.ConfigureServices(serviceCollection)
        
        let serviceProvider = serviceCollection.BuildServiceProvider()
        serviceCollection.AddSingleton<IServiceProvider>(serviceProvider) |> ignore
        projectionsFactory <- serviceProvider.GetRequiredService<IProjectionsFactory>()
        projectionsStore <- serviceProvider.GetRequiredService<IProjectionsStore>()
    )

    let GetIdFromObject(obj: obj) =
        let property = obj.GetType().GetProperty("Id")
        if property = null then
            raise (System.InvalidOperationException($"Type {obj.GetType().Name} does not have an 'Id' property"))
        let value = property.GetValue(obj)
        IdValidator.ValidateIdType(value)
        value

    member this.ProjectionStore = 
        let _ = lazyServices.Value
        projectionsStore
        
    member this.ProjectionsFactory = 
        let _ = lazyServices.Value
        projectionsFactory

    member this.Given([<ParamArrayAttribute>] args : 'TEvent[]) =
        task {
            let projectionType = typeof<'TProjection>
            let! projection = this.ProjectionsFactory.CreateAsync(projectionType).ConfigureAwait(false)
            let projectionInstance = projection :?> IProjectionInstance<'TEvent>
            let subscription = projectionInstance.Subscription :?> TestKitSubscription<'TEvent>
            subscription.StoreEvents(args)
            do! projection.StartAsync().ConfigureAwait(false)
        }

    member this.Then<'TEntity when 'TEntity : not struct>(expectedEntity: 'TEntity) =
        task {
            let id = GetIdFromObject(expectedEntity)
            TestValid <- true
            DocumentId <- id
            let! entity = this.ProjectionStore.LoadAsync<'TEntity>(id).ConfigureAwait(false)
            let difference = ObjectComparer.DeepCompare(expectedEntity, entity)
            if difference <> String.Empty then
                raise (XunitException(difference))
        }

    member private this.ResetState() =
        this.ProjectionStore.DeleteAsync(DocumentId).GetAwaiter().GetResult()
        DocumentId <- obj
        TestValid <- false

    interface IDisposable with
        member this.Dispose() = 
            if not TestValid then
                this.ResetState()
                raise (XunitException("[TEST INVALID]: Then was not called!"))
            this.ResetState()

    abstract ConfigureServices : services: IServiceCollection -> unit
    default _.ConfigureServices(services: IServiceCollection) = ()