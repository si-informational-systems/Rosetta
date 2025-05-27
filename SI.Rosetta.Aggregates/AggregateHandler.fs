namespace SI.Rosetta.Aggregates

open System
open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Common

[<AbstractClass>]
type AggregateHandler<'TAggregate, 'TCommands, 'TEvents when 'TAggregate :> IAggregateInstance<'TCommands> 
                                        and 'TAggregate : (new : unit -> 'TAggregate)
                                        and 'TAggregate : null
                                        and 'TCommands :> IAggregateCommands
                                        and 'TEvents :> IAggregateEvents>() =
    let mutable publishedEvents = List<IAggregateEvents>()
    let mutable aggregateRepository = Unchecked.defaultof<IAggregateRepository>
    let NotFoundResponse = 
        let aggregateName = 
            typeof<'TAggregate>.Name.Replace("Aggregate", "", StringComparison.InvariantCultureIgnoreCase)
        sprintf "%sDoesNotExist" aggregateName

    abstract ExecuteAsync: command: 'TCommands -> Task<unit>

    member private this.Execute(arg: ICommands) : Task<unit> = 
        task {
            do! this.ExecuteAsync(arg :?> 'TCommands).ConfigureAwait(false)
        }
    
    member this.AggregateRepository
        with get() = aggregateRepository
        and set value = aggregateRepository <- value

    interface IAggregateHandler with
        member this.ExecuteAsync(arg) = this.Execute(arg)
        member this.GetPublishedEvents() = publishedEvents

    member this.IdempotentlyCreateAggregate (id: obj) (command: 'TCommands) =
        task {
            let! agg = aggregateRepository.GetAsync<'TAggregate, 'TEvents>(id).ConfigureAwait(false)
            let mutable aggregate = if (agg :> obj) = null then new 'TAggregate() else agg
            let originalVersion = aggregate.Version
            
            aggregate.Execute command
            publishedEvents <- aggregate.PublishedEvents
            
            if originalVersion <> aggregate.Version then
                do! aggregateRepository.StoreAsync(aggregate).ConfigureAwait(false)
        }
        
    member this.IdempotentlyUpdateAggregate (id: obj) (command: 'TCommands) =
        task {
            let! agg = aggregateRepository.GetAsync<'TAggregate, 'TEvents>(id).ConfigureAwait(false)
            if isNull agg then
                raise (DomainException.Named(NotFoundResponse, String.Empty))
                
            let originalVersion = agg.Version
            
            agg.Execute command
            publishedEvents <- agg.PublishedEvents
            
            if originalVersion <> agg.Version then
                do! aggregateRepository.StoreAsync(agg).ConfigureAwait(false)
        }