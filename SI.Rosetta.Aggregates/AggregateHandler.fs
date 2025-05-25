namespace SI.Rosetta.Aggregates

open System
open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Common

[<AbstractClass>]
type AggregateHandler<'TAggregate, 'TCommands, 'TEvents when 'TAggregate :> IAggregateInstance<'TCommands> 
                                        and 'TAggregate : (new : unit -> 'TAggregate)
                                        and 'TAggregate : null
                                        and 'TCommands :> ICommands
                                        and 'TEvents :> IEvents>() =
    let mutable publishedEvents = List<IEvents>()
    let mutable aggregateRepository = Unchecked.defaultof<IAggregateRepository>
    let NotFoundResponse = 
        let aggregateName = 
            typeof<'TAggregate>.Name.Replace("Aggregate", "", StringComparison.InvariantCultureIgnoreCase)
        sprintf "%sDoesNotExist" aggregateName

    abstract ExecuteAsync: command: 'TCommands -> Task<unit>

    member private this.IExecute(arg: ICommands) : Task<unit> = 
        task {
            do! this.ExecuteAsync(arg :?> 'TCommands)
        }
    
    member this.AggregateRepository
        with get() = aggregateRepository
        and set value = aggregateRepository <- value

    interface IAggregateHandler with
        member this.ExecuteAsync(arg) = this.IExecute(arg)
        member this.GetPublishedEvents() = publishedEvents

    member this.IdempotentlyCreateAggregate (id: string) (command: 'TCommands) =
        task {
            let! agg = aggregateRepository.GetAsync<'TAggregate, 'TEvents> id
            let mutable aggregate = if (agg :> obj) = null then new 'TAggregate() else agg
            let originalVersion = aggregate.Version
            
            aggregate.Execute command
            publishedEvents <- aggregate.PublishedEvents
            
            if originalVersion <> aggregate.Version then
                do! aggregateRepository.StoreAsync aggregate
        }
        
    member this.IdempotentlyUpdateAggregate (id: string) (command: 'TCommands) =
        task {
            let! agg = aggregateRepository.GetAsync<'TAggregate, 'TEvents> id
            if isNull agg then
                raise (DomainException.Named(NotFoundResponse, String.Empty))
                
            let originalVersion = agg.Version
            
            agg.Execute command
            publishedEvents <- agg.PublishedEvents
            
            if originalVersion <> agg.Version then
                do! aggregateRepository.StoreAsync agg
        }