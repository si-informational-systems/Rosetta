namespace SI.Rosetta.Aggregates.TestKit

open SI.Rosetta.Common
open System.Collections.Generic

type ThenResult = {
    Expectation: string
    Failure: string option
}

type HandlerExecutedCommandResult = {
    ProducedEvents: List<IAggregateEvents>
    PublishedEvents: List<IAggregateEvents>
}