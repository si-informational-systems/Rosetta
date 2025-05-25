namespace SI.Rosetta.Projections.EventStore

open System

type EventStoreProjectionParameters = {
    ProjectionName: string
    DestinationStreamName: string
    AggregateEventsStreamNames: string list
    EventsToInclude: Type array
}