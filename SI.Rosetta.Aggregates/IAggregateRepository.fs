namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common
open System.Threading.Tasks
open System.Runtime.InteropServices
open System

type IAggregateRepository =
    abstract member StoreAsync<'TAggregate, 'TAggregateState
            when 'TAggregate :> IAggregate<'TAggregateState>> : 
            aggregate: 'TAggregate -> Task
    abstract member GetAsync<'TAggregate, 'TAggregateState, 'TEvents
            when 'TAggregate : (new : unit -> 'TAggregate) 
            and 'TAggregate :> IAggregate<'TAggregateState>
            and 'TAggregate : not struct
            and 'TAggregateState : (new : unit -> 'TAggregateState)
            and 'TAggregateState :> IAggregateStateInstance<'TEvents>
            and 'TEvents :> IAggregateEvents> : 
        id: string * [<Optional; DefaultParameterValue(Int64.MaxValue)>] version: int64 -> Task<Option<'TAggregate>>

type IEventSourcedAggregateRepository = 
    inherit IAggregateRepository

type IStateBasedAggregateRepository = 
    inherit IAggregateRepository