namespace SI.Rosetta.Projections

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open SI.Rosetta.Common

type Projection<'TEvent when 'TEvent :> IAggregateEvents>() =
    // A commit that failed may still have been written (the reply was lost): the event's next attempt checks first
    let mutable CommitUncertain = false

    member val Name = "" with get, set
    member val SubscriptionStreamName = "" with get, set
    member val Subscription = Unchecked.defaultof<ISubscription<'TEvent>> with get, set
    member val Handlers = Unchecked.defaultof<IEnumerable<IProjectionHandler<'TEvent>>> with get, set
    member val CheckpointStore = Unchecked.defaultof<ICheckpointStore> with get, set
    member val Checkpoint = Unchecked.defaultof<Checkpoint> with get, set
    member val SkipUnknownEvents = false with get, set
    /// Commits each event's writes with its checkpoint. Without one, the handlers write straight to the store and the
    /// checkpoint is stored after them, in its own write
    /// Begins each event's transaction, which its handlers write in and its checkpoint is committed with. Without one,
    /// the handlers write straight to the store and the checkpoint is stored after them, in its own write
    member val UnitOfWork = Unchecked.defaultof<IProjectionsUnitOfWork> with get, set

    member this.CreateProjectionException (ev: obj) (checkpoint: uint64) (ae: AggregateException) =
        let msg = sprintf "PROJECTION FAILURE: Projection %s on Stream %s FAILED on Checkpoint %d during %s" 
                    this.Name this.SubscriptionStreamName checkpoint (ev.GetType().FullName)
        let ex = ProjectionException(msg, ae)
        ex.ProjectionName <- this.Name
        ex.EventTypeName <- ev.GetType().FullName
        ex.Checkpoint <- checkpoint
        ex.SubscriptionStreamName <- this.SubscriptionStreamName
        ex

    /// Runs the handlers one at a time, in the order the projection declares them, so a handler sees what the ones
    /// before it wrote. The first one that fails stops the event: a ProjectionException holds its failure
    member private this.RunHandlers (ev: 'TEvent) (c: uint64) : Task =
        task {
            for handler in this.Handlers do
                try
                    // A handler that throws before returning its Task fails like one whose Task faults
                    do! handler.Handle(ev, c).ConfigureAwait(false)
                with ex ->
                    raise (this.CreateProjectionException ev c (AggregateException(ex)))
        }

    /// Whether a commit that failed on this event was written after all: the stored checkpoint has reached it
    member private this.CommittedBefore(c: uint64) =
        task {
            if not CommitUncertain then
                return false
            else
                let! stored = this.CheckpointStore.ReadAsync(this.Checkpoint.Id).ConfigureAwait(false)
                CommitUncertain <- false
                return stored.Value >= c
        }

    member private this.HandleInTransaction (ev: 'TEvent) (c: uint64) (checkpoint: Checkpoint) =
        task {
            let! transaction = this.UnitOfWork.BeginAsync().ConfigureAwait(false)
            let mutable failure: exn = null
            try
                do! ProjectionTransaction.Within(transaction, fun () -> this.RunHandlers ev c).ConfigureAwait(false)
                try
                    do! transaction.CommitAsync(checkpoint).ConfigureAwait(false)
                with ex ->
                    CommitUncertain <- true
                    failure <- ex
            with ex -> failure <- ex
            // Rolls back what wasn't committed
            do! transaction.DisposeAsync().ConfigureAwait(false)
            if not (isNull failure) then
                ExceptionDispatchInfo.Throw failure
        }

    /// Handles the event in one transaction: its handlers, one at a time, then the checkpoint, committed together.
    /// When a handler fails, nothing is written and a ProjectionException holds its failure
    member this.HandleEvent(ev: 'TEvent, c: uint64) =
        task {
            let started = Stopwatch.GetTimestamp()
            let projectionTag = RosettaTelemetry.Tag "rosetta.projection" this.Name
            let! committed = this.CommittedBefore(c).ConfigureAwait(false)
            if committed then
                this.Checkpoint.Value <- c
            else
                let checkpoint = Checkpoint(Id = this.Checkpoint.Id, Value = c, Version = this.Checkpoint.Version)
                try
                    if isNull (box this.UnitOfWork) then
                        do! this.RunHandlers ev c
                        do! this.CheckpointStore.StoreAsync(checkpoint).ConfigureAwait(false)
                    else
                        do! this.HandleInTransaction ev c checkpoint
                with :? ProjectionException as ex ->
                    RosettaTelemetry.ProjectionEvents.Add(1L, projectionTag, RosettaTelemetry.Tag "rosetta.outcome" "failed")
                    ExceptionDispatchInfo.Throw ex
                this.Checkpoint.Value <- c
                RosettaTelemetry.ProjectionEvents.Add(1L, projectionTag, RosettaTelemetry.Tag "rosetta.outcome" "handled")
                RosettaTelemetry.ProjectionEventDuration.Record(RosettaTelemetry.SecondsSince started, projectionTag)
        }
        
    interface IProjectionOf<'TEvent> with
        member this.EventsType = typeof<'TEvent>

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