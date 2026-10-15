namespace SI.Rosetta.Projections.KurrentDB

open System

type KurrentDBProjectionParameters = {
    ProjectionName: string
    DestinationStreamName: string
    AggregateEventsStreamNames: string list
    EventsToInclude: Type array
}