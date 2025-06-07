namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common
open System.Threading.Tasks
open System.Runtime.InteropServices

type IAggregateRepository =
    abstract member StoreAsync: aggregate: IAggregate -> Task
    abstract member GetAsync<'TAggregate, 'TEvents
                                        when 'TAggregate : (new : unit -> 'TAggregate) 
                                        and 'TAggregate :> IAggregate
                                        and 'TAggregate : not struct
                                        and 'TEvents :> IAggregateEvents> : 
        id: obj * [<Optional; DefaultParameterValue(2147483647)>] version: int -> Task<Option<'TAggregate>>