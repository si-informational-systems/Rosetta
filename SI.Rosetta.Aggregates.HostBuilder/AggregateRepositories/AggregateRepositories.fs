namespace SI.Rosetta.Aggregates.HostBuilder

type EventSourcedAggregateRepository() = class end 

type EventStore() =
    inherit EventSourcedAggregateRepository()

type StateBasedAggregateRepository() = class end 

type RavenDB() =
    inherit StateBasedAggregateRepository()
