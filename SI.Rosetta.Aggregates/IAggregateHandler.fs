namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common
open System.Collections.Generic
open System.Threading.Tasks

type IAggregateHandler =
    abstract member GetPublishedEvents: unit -> List<IEvents> 
    abstract member ExecuteAsync: ICommands -> Task<unit>