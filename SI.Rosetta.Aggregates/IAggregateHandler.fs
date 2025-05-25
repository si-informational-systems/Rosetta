namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common
open System.Collections.Generic
open System.Threading.Tasks

type IAggregateHandler =
    abstract member GetPublishedEvents: unit -> List<IAggregateEvents> 
    abstract member ExecuteAsync: IAggregateCommands -> Task<unit>