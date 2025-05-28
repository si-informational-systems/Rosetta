namespace SI.Rosetta.Projections

open System
open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Common

type Projection<'TEvent when 'TEvent :> IEvents>() =
    member val Name = "" with get, set
    member val SubscriptionStreamName = "" with get, set
    member val Subscription = Unchecked.defaultof<ISubscription<'TEvent>> with get, set
    member val Handlers = Unchecked.defaultof<IEnumerable<IProjectionHandler<'TEvent>>> with get, set
    member val CheckpointStore = Unchecked.defaultof<ICheckpointStore> with get, set
    member val Checkpoint = Unchecked.defaultof<Checkpoint> with get, set
    
    member this.CreateProjectionException (ev: obj) (checkpoint: uint64) (ae: AggregateException) =
        let msg = sprintf "PROJECTION FAILURE: Projection %s on Stream %s FAILED on Checkpoint %d during %s" 
                    this.Name this.SubscriptionStreamName checkpoint (ev.GetType().FullName)
        let ex = ProjectionException(msg, ae)
        ex.ProjectionName <- this.Name
        ex.EventTypeName <- ev.GetType().FullName
        ex.Checkpoint <- checkpoint
        ex.SubscriptionStreamName <- this.SubscriptionStreamName
        ex
        
    member this.StartHandlingTasks (e: 'TEvent) (c: uint64) =
        let tasks = List<Task>()
        for d in this.Handlers do
            tasks.Add(d.Handle(e, c))
        tasks.ToArray()
        
    member this.HandleEvent(ev: 'TEvent, c: uint64) =
        task {
            this.Checkpoint.Value <- c
            Task.WaitAll(this.StartHandlingTasks ev c)
            do! this.CheckpointStore.WriteAsync this.Checkpoint
        }
        
    interface IProjectionInstance<'TEvent> with
        member this.Name 
            with get() = this.Name
            and set value = this.Name <- value
            
        member this.Subscription
            with get() = this.Subscription
            and set value = this.Subscription <- value

        member this.SubscriptionStreamName
            with get() = this.SubscriptionStreamName
            and set value = this.SubscriptionStreamName <- value
            
        member this.Handlers
            with get() = this.Handlers
            and set value = this.Handlers <- value
            
        member this.Checkpoint
            with get() = this.Checkpoint
            and set value = this.Checkpoint <- value

        member this.ProjectAsync
            with get() = this.ProjectAsync
            
        member this.StartAsync() =
            task {
                do! this.Subscription.StartAsync this.Checkpoint.Value
            }
        
    member private this.TryHandleEvent(ev: 'TEvent, checkpoint: uint64) =
        task {
            try
                do! this.HandleEvent(ev, checkpoint)
            with
            | :? AggregateException as ex ->
                raise (this.CreateProjectionException ev checkpoint ex)
        }

    member this.ProjectAsync(ev: 'TEvent, checkpoint: uint64) = 
        task {
            do! this.TryHandleEvent(ev, checkpoint)
        }