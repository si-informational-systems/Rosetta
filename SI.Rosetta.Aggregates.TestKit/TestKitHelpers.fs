namespace SI.Rosetta.Aggregates.TestKit

open SI.Rosetta.Common

[<AutoOpen>]
module TestKitHelpers =
    let NoProducedEvents = ResizeArray<IAggregateEvents>()
    let NoPublishedEvents = ResizeArray<IAggregateEvents>()
