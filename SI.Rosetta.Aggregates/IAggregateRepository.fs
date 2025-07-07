namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common
open System.Threading.Tasks
open System.Runtime.InteropServices
open System

type IAggregateRepository =
    abstract member StoreAsync: aggregate: IAggregate -> Task
    abstract member GetAsync<'TAggregate, 'TEvents
            when 'TAggregate : (new : unit -> 'TAggregate) 
            and 'TAggregate :> IAggregate
            and 'TAggregate : not struct
            and 'TEvents :> IAggregateEvents> : 
        id: string * [<Optional; DefaultParameterValue(Int64.MaxValue)>] version: int64 -> Task<Option<'TAggregate>>

type IEventSourcedAggregateRepository = 
    inherit IAggregateRepository

type IStateBasedAggregateRepository = 
    inherit IAggregateRepository