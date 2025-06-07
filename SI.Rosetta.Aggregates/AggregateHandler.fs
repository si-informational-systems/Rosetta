namespace SI.Rosetta.Aggregates

open System
open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Common

[<AbstractClass>]
type AggregateHandler<'TAggregate, 'TCommands, 'TEvents when 'TAggregate :> IAggregateInstance<'TCommands> 
                                        and 'TAggregate : (new : unit -> 'TAggregate)
                                        and 'TAggregate : not struct
                                        and 'TCommands :> IAggregateCommands
                                        and 'TEvents :> IAggregateEvents>() =
    let mutable publishedEvents = List<IAggregateEvents>()
    let mutable aggregateRepository = Unchecked.defaultof<IAggregateRepository>
    let NotFoundResponse = 
        let aggregateName = 
            typeof<'TAggregate>.Name.Replace("Aggregate", "", StringComparison.InvariantCultureIgnoreCase)
        sprintf "%sDoesNotExist" aggregateName

    abstract ExecuteAsync: command: 'TCommands -> Task<unit>
    
    member this.AggregateRepository
        with get() = aggregateRepository
        and set value = aggregateRepository <- value

    interface IAggregateHandler with
        member this.ExecuteAsync(arg) = this.ExecuteAsync(arg :?> 'TCommands)
        member this.GetPublishedEvents() = publishedEvents

    member this.IdempotentlyCreateAggregate (id: obj) (command: 'TCommands) =
        task {
            let! aggOpt = aggregateRepository.GetAsync<'TAggregate, 'TEvents>(id).ConfigureAwait(false)
            let aggregate = 
                match aggOpt with
                | None -> new 'TAggregate()
                | Some agg -> agg
            let originalVersion = aggregate.Version
            
            aggregate.Execute command
            publishedEvents <- aggregate.PublishedEvents
            
            if originalVersion <> aggregate.Version then
                do! aggregateRepository.StoreAsync(aggregate).ConfigureAwait(false)
        }
        
    member this.IdempotentlyUpdateAggregate (id: obj) (command: 'TCommands) =
        task {
            let! aggOpt = aggregateRepository.GetAsync<'TAggregate, 'TEvents>(id).ConfigureAwait(false)
            match aggOpt with
            | None -> raise (DomainException.Named(NotFoundResponse, String.Empty))
            | Some agg ->
                let originalVersion = agg.Version
                agg.Execute command
                publishedEvents <- agg.PublishedEvents
                
                if originalVersion <> agg.Version then
                    do! aggregateRepository.StoreAsync(agg).ConfigureAwait(false)
        }