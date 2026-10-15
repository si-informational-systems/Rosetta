namespace SI.Rosetta.Aggregates.HostBuilder

type EventSourcedAggregateRepository() = class end 

type KurrentDB() =
    inherit EventSourcedAggregateRepository()

type StateBasedAggregateRepository() = class end 

type RavenDB() =
    inherit StateBasedAggregateRepository()
    
type MongoDB() =
    inherit StateBasedAggregateRepository()
