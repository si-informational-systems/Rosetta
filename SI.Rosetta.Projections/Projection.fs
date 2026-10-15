namespace SI.Rosetta.Projections

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks
open SI.Rosetta.Common

type Projection<'TEvent when 'TEvent :> IAggregateEvents>() =
    member val Name = "" with get, set
    member val SubscriptionStreamName = "" with get, set
    member val Subscription = Unchecked.defaultof<ISubscription<'TEvent>> with get, set
    member val Handlers = Unchecked.defaultof<IEnumerable<IProjectionHandler<'TEvent>>> with get, set
    member val CheckpointStore = Unchecked.defaultof<ICheckpointStore> with get, set
    member val Checkpoint = Unchecked.defaultof<Checkpoint> with get, set
    member val SkipUnknownEvents = false with get, set

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
            // A handler that throws before returning its Task fails like one whose Task faults
            tasks.Add(try d.Handle(e, c) with ex -> Task.FromException ex)
        tasks.ToArray()

    /// Runs every handler on the event in parallel, then stores the checkpoint.
    /// Throws a ProjectionException holding every handler's failure when any fails
    member this.HandleEvent(ev: 'TEvent, c: uint64) =
        task {
            let started = Stopwatch.GetTimestamp()
            let projectionTag = RosettaTelemetry.Tag "rosetta.projection" this.Name
            this.Checkpoint.Value <- c
            let handling = Task.WhenAll(this.StartHandlingTasks ev c)
            try
                do! handling.ConfigureAwait(false)
            with _ when handling.IsFaulted ->
                RosettaTelemetry.ProjectionEvents.Add(1L, projectionTag, RosettaTelemetry.Tag "rosetta.outcome" "failed")
                // Awaiting rethrows only the first failure: handling.Exception has them all
                raise (this.CreateProjectionException ev c handling.Exception)
            do! this.CheckpointStore.StoreAsync(this.Checkpoint).ConfigureAwait(false)
            RosettaTelemetry.ProjectionEvents.Add(1L, projectionTag, RosettaTelemetry.Tag "rosetta.outcome" "handled")
            RosettaTelemetry.ProjectionEventDuration.Record(RosettaTelemetry.SecondsSince started, projectionTag)
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
            
        member this.StartAsync(cancellationToken) =
            task {
                do! this.Subscription.StartAsync(this.Checkpoint.Value, cancellationToken)
            }
        
    member this.ProjectAsync(ev: 'TEvent, checkpoint: uint64) =
        task {
            do! this.HandleEvent(ev, checkpoint).ConfigureAwait(false)
        }