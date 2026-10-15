namespace SI.Rosetta.Aggregates.HostBuilder

// The stores Aggregates can use. The set is closed: the bases can't be inherited outside Rosetta (internal
// constructors), the stores can't be inherited at all (sealed), and the Use…/Add… functions require a public
// parameterless constructor, which the bases don't have. So only these types compile as their store arguments

/// The event-sourced stores
type EventSourcedAggregateRepository internal () = class end

[<Sealed>]
type KurrentDB() =
    inherit EventSourcedAggregateRepository()

/// The state-based stores
type StateBasedAggregateRepository internal () = class end

[<Sealed>]
type RavenDB() =
    inherit StateBasedAggregateRepository()

[<Sealed>]
type MongoDB() =
    inherit StateBasedAggregateRepository()
