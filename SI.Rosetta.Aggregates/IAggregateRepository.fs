namespace SI.Rosetta.Aggregates

open System.Threading.Tasks
open System.Runtime.InteropServices
open System

type IAggregateRepository =
    /// Stores the aggregate's new events (event-sourced) or its state (state-based)
    abstract member StoreAsync<'TAggregateState
            when 'TAggregateState :> IAggregateState> :
            aggregate: IAggregate<'TAggregateState> -> Task
    /// The aggregate's state, rebuilt from its events up to version (event-sourced) or as stored (state-based).
    /// None when the aggregate doesn't exist
    abstract member GetAsync<'TAggregateState
            when 'TAggregateState : (new : unit -> 'TAggregateState)
            and 'TAggregateState :> IAggregateState> :
        id: string * [<Optional; DefaultParameterValue(Int64.MaxValue)>] version: int64 -> Task<Option<'TAggregateState>>

type IEventSourcedAggregateRepository = 
    inherit IAggregateRepository

type IStateBasedAggregateRepository = 
    inherit IAggregateRepository
