namespace SI.Rosetta.Aggregates

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open SI.Rosetta.Common

[<AbstractClass>]
type AggregateHandler<'TAggregate, 'TAggregateState, 'TCommands, 'TEvents 
        when 'TAggregate :> IAggregateInstance<'TAggregateState, 'TCommands> 
        and 'TAggregate : (new : unit -> 'TAggregate)
        and 'TAggregate : not struct
        and 'TAggregateState : (new : unit -> 'TAggregateState)
        and 'TAggregateState :> IAggregateStateInstance<'TEvents>
        and 'TCommands :> IAggregateCommands
        and 'TEvents :> IAggregateEvents>() =

    let AggregateName = typeof<'TAggregate>.Name.Replace("Aggregate", "", StringComparison.InvariantCultureIgnoreCase)
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

    /// Loads the aggregate (a new one when it doesn't exist and mustExist is false), executes the command and stores
    /// the aggregate when its version changed. Traced as "{Aggregate} {Command}" and counted in the rosetta.* metrics
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
                    let! aggOpt = this.AggregateRepository.GetAsync<'TAggregate, 'TAggregateState, 'TEvents>(id).ConfigureAwait(false)
                    let aggregate =
                        match aggOpt with
                        | Some agg -> agg
                        | None when mustExist -> raise (DomainException.Named(NotFoundResponse, String.Empty))
                        | None -> new 'TAggregate()
                    let originalVersion = aggregate.Version

                    aggregate.Execute command
                    this.PublishedEvents <- aggregate.PublishedEvents

                    if originalVersion <> aggregate.Version then
                        do! this.AggregateRepository.StoreAsync<'TAggregate, 'TAggregateState>(aggregate).ConfigureAwait(false)
                        let stored = aggregate.Version - originalVersion
                        RosettaTelemetry.EventsStored.Add(stored, RosettaTelemetry.Tag "rosetta.aggregate" AggregateName)
                        if not (isNull activity) then activity.SetTag("rosetta.events", stored) |> ignore
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