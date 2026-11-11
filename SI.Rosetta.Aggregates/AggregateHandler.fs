namespace SI.Rosetta.Aggregates

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open SI.Rosetta.Common

[<AbstractClass>]
type AggregateHandler<'TAggregate, 'TCommands
        when 'TAggregate :> IAggregateInstance<'TCommands>
        and 'TAggregate : (new : unit -> 'TAggregate)
        and 'TAggregate : not struct
        and 'TCommands :> IAggregateCommands>() =

    let AggregateName = AggregateNameOf typeof<'TAggregate>
    let NotFoundResponse = sprintf "%sDoesNotExist" AggregateName

    /// The command's record type name (RegisterPerson), for traces and metrics
    let CommandName (command: 'TCommands) =
        let case, fields = FSharpValue.GetUnionFields(command, command.GetType())
        if fields.Length = 1 && not (isNull fields.[0]) then fields.[0].GetType().Name else case.Name

    member val PublishedEvents = List<IAggregateEvents>() with get, set
    member val AggregateRepository = Unchecked.defaultof<IAggregateRepository> with get, set
    
    abstract ExecuteAsync: command: 'TCommands -> Task<unit>
    
    interface IAggregateHandler with
        member this.ExecuteAsync(arg) = this.ExecuteAsync(arg :?> 'TCommands)
        member this.GetPublishedEvents() = this.PublishedEvents

    /// How a command that hit a conflict (another command stored the aggregate first) is retried at once, on the
    /// aggregate loaded again, before the caller sees the ConcurrencyException: 3 times, after up to 50 ms each, unless
    /// set otherwise (ConflictRetries.None turns it off)
    member val ConflictRetries = ConflictRetries.Default with get, set

    /// One attempt: loads the aggregate (a new one when it doesn't exist and mustExist is false), executes the command
    /// and stores the aggregate when its version changed. When it didn't, a repository with an outbox records the
    /// events the command published
    member private this.Attempt (id: string) (command: 'TCommands) (mustExist: bool) (activity: Activity) : Task =
        task {
            let aggregate = new 'TAggregate()
            let! exists = aggregate.LoadAsync(this.AggregateRepository, id).ConfigureAwait(false)
            if mustExist && not exists then
                raise (DomainException.Named(NotFoundResponse, String.Empty))
            let originalVersion = aggregate.Version

            aggregate.Execute command
            this.PublishedEvents <- aggregate.PublishedEvents

            if originalVersion <> aggregate.Version then
                do! aggregate.StoreAsync(this.AggregateRepository).ConfigureAwait(false)
                let stored = aggregate.Version - originalVersion
                RosettaTelemetry.EventsStored.Add(stored, RosettaTelemetry.Tag "rosetta.aggregate" AggregateName)
                if not (isNull activity) then activity.SetTag("rosetta.events", stored) |> ignore
            elif aggregate.PublishedEvents.Count > 0 then
                // Nothing to store, e.g. an idempotent repeat, but the events it published again are recorded unless
                // the outbox holds them: the attempt that stored the aggregate may have failed before it recorded them
                match this.AggregateRepository with
                | :? IRecordPublishedEvents as outbox ->
                    do! outbox.RecordPublishedEventsAsync(aggregate).ConfigureAwait(false)
                | _ -> ()
        } :> Task

    /// The command, attempted again on a conflict (ConflictRetries) until it goes through or the retries run out.
    /// Traced as "{Aggregate} {Command}" and counted in the rosetta.* metrics
    member private this.Handle (id: string) (command: 'TCommands) (mustExist: bool) =
        task {
            let commandName = CommandName command
            use activity = RosettaTelemetry.ActivitySource.StartActivity(sprintf "%s %s" AggregateName commandName)
            if not (isNull activity) then
                activity.SetTag("rosetta.aggregate", AggregateName).SetTag("rosetta.aggregate.id", id).SetTag("rosetta.command", commandName) |> ignore
            let started = Stopwatch.GetTimestamp()
            let mutable outcome = "handled"
            try
                try
                    let retries = this.ConflictRetries
                    let mutable attempt = 0
                    let mutable handled = false
                    while not handled do
                        let mutable retry = false
                        try
                            do! this.Attempt id command mustExist activity
                            handled <- true
                        with ex when attempt < retries.Retries && ExceptionCauses.IsConcurrencyConflict ex ->
                            retry <- true
                        if retry then
                            attempt <- attempt + 1
                            RosettaTelemetry.RetriedConflicts.Add(1L, RosettaTelemetry.Tag "rosetta.aggregate" AggregateName)
                            if not (isNull activity) then
                                activity.AddEvent(ActivityEvent("rosetta.conflict.retry")).SetTag("rosetta.attempts", attempt + 1) |> ignore
                            do! retries.PauseAsync(CancellationToken.None).ConfigureAwait(false)
                with
                | :? DomainException as ex ->
                    outcome <- "rejected"
                    if not (isNull activity) then
                        activity.SetStatus(ActivityStatusCode.Error, ex.Name).SetTag("rosetta.domain_error", ex.Name) |> ignore
                    ExceptionDispatchInfo.Throw ex
                | ex ->
                    outcome <- "failed"
                    if not (isNull activity) then
                        activity.SetStatus(ActivityStatusCode.Error, ex.Message).AddException(ex) |> ignore
                    ExceptionDispatchInfo.Throw ex
            finally
                if not (isNull activity) then activity.SetTag("rosetta.outcome", outcome) |> ignore
                let aggregateTag = RosettaTelemetry.Tag "rosetta.aggregate" AggregateName
                let commandTag = RosettaTelemetry.Tag "rosetta.command" commandName
                RosettaTelemetry.Commands.Add(1L, aggregateTag, commandTag, RosettaTelemetry.Tag "rosetta.outcome" outcome)
                RosettaTelemetry.CommandDuration.Record(RosettaTelemetry.SecondsSince started, aggregateTag, commandTag)
        }

    member this.IdempotentlyCreateAggregate (id: string) (command: 'TCommands) =
        this.Handle id command false
        
    member this.IdempotentlyUpdateAggregate (id: string) (command: 'TCommands) =
        this.Handle id command true