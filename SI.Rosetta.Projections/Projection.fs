namespace SI.Rosetta.Projections

open System
open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Common

type Projection<'TEvent when 'TEvent :> IEvents>() =
    let mutable name = ""
    let mutable subscriptionStreamName = ""
    let mutable subscription = Unchecked.defaultof<ISubscription<'TEvent>>
    let mutable handlers = Unchecked.defaultof<IEnumerable<IProjectionHandler<'TEvent>>>
    let mutable checkpointWriter = Unchecked.defaultof<ICheckpointWriter>
    let mutable checkpoint = Unchecked.defaultof<Checkpoint>
    
    let CreateProjectionException (ev: obj) (checkpoint: uint64) (ae: AggregateException) =
        let msg = sprintf "PROJECTION FAILURE: Projection %s on Stream %s FAILED on Checkpoint %d during %s" 
                    name subscriptionStreamName checkpoint (ev.GetType().FullName)
        let ex = ProjectionException(msg, ae)
        ex.ProjectionName <- name
        ex.EventTypeName <- ev.GetType().FullName
        ex.Checkpoint <- checkpoint
        ex.SubscriptionStreamName <- subscriptionStreamName
        ex
        
    let StartHandlingTasks (e: 'TEvent) (c: uint64) =
        let tasks = List<Task>()
        for d in handlers do
            tasks.Add(d.Handle(e, c))
        tasks.ToArray()
        
    let handleEvent(ev: 'TEvent, c: uint64) =
        task {
            checkpoint.Value <- c
            Task.WaitAll(StartHandlingTasks ev c)
            do! checkpointWriter.Write checkpoint
        }
        
    interface IProjectionInstance<'TEvent> with
        member this.Name 
            with get() = name
            and set value = name <- value
            
        member this.Subscription
            with get() = subscription
            and set value = subscription <- value

        member this.SubscriptionStreamName
            with get() = subscriptionStreamName
            and set value = subscriptionStreamName <- value
            
        member this.Handlers
            with get() = handlers
            and set value = handlers <- value
            
        member this.Checkpoint
            with get() = checkpoint
            and set value = checkpoint <- value

        member this.ProjectAsync
            with get() = this.ProjectAsync
            
        member this.StartAsync() =
            task {
                do! subscription.StartAsync checkpoint.Value
            }
            
    member this.Name
        with get() = name
        and set value = name <- value
        
    member this.SubscriptionStreamName
        with get() = subscriptionStreamName
        and set value = subscriptionStreamName <- value
        
    member this.Subscription
        with get() = subscription
        and set value = subscription <- value
        
    member this.Handlers
        with get() = handlers
        and set value = handlers <- value
        
    member this.CheckpointWriter
        with get() = checkpointWriter
        and set value = checkpointWriter <- value
        
    member this.Checkpoint
        with get() = checkpoint
        and set value = checkpoint <- value 
        
    member private this.TryHandleEvent(ev: 'TEvent, checkpoint: uint64) =
        task {
            try
                do! handleEvent(ev, checkpoint)
            with
            | :? AggregateException as ex ->
                raise (CreateProjectionException ev checkpoint ex)
        }

    member this.ProjectAsync(ev: 'TEvent, checkpoint: uint64) = 
        task {
            do! this.TryHandleEvent(ev, checkpoint)
        }