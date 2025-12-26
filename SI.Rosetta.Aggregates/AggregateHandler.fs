namespace SI.Rosetta.Aggregates

open System
open System.Collections.Generic
open System.Threading.Tasks
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

    let NotFoundResponse = 
        let aggregateName = 
            typeof<'TAggregate>.Name.Replace("Aggregate", "", StringComparison.InvariantCultureIgnoreCase)
        sprintf "%sDoesNotExist" aggregateName

    member val PublishedEvents = List<IAggregateEvents>() with get, set
    member val AggregateRepository = Unchecked.defaultof<IAggregateRepository> with get, set
    
    abstract ExecuteAsync: command: 'TCommands -> Task<unit>
    
    interface IAggregateHandler with
        member this.ExecuteAsync(arg) = this.ExecuteAsync(arg :?> 'TCommands)
        member this.GetPublishedEvents() = this.PublishedEvents

    member this.IdempotentlyCreateAggregate (id: string) (command: 'TCommands) =
        task {
            let! aggOpt = this.AggregateRepository.GetAsync<'TAggregate, 'TAggregateState, 'TEvents>(id).ConfigureAwait(false)
            let aggregate = 
                match aggOpt with
                | None -> new 'TAggregate()
                | Some agg -> agg
            let originalVersion = aggregate.Version
            
            aggregate.Execute command
            this.PublishedEvents <- aggregate.PublishedEvents
            
            if originalVersion <> aggregate.Version then
                do! this.AggregateRepository.StoreAsync<'TAggregate, 'TAggregateState>(aggregate).ConfigureAwait(false)
        }
        
    member this.IdempotentlyUpdateAggregate (id: string) (command: 'TCommands) =
        task {
            let! aggOpt = this.AggregateRepository.GetAsync<'TAggregate, 'TAggregateState, 'TEvents>(id).ConfigureAwait(false)
            match aggOpt with
            | None -> raise (DomainException.Named(NotFoundResponse, String.Empty))
            | Some agg ->
                let originalVersion = agg.Version
                agg.Execute command
                this.PublishedEvents <- agg.PublishedEvents
                
                if originalVersion <> agg.Version then
                    do! this.AggregateRepository.StoreAsync<'TAggregate, 'TAggregateState>(agg).ConfigureAwait(false)
        }