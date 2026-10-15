namespace SI.Rosetta.Projections.HostBuilder

// The event stores Projections can subscribe to: a closed set, like the ProjectionStores

/// The event stores
type EventRepository internal () = class end

[<Sealed>]
type KurrentDB() =
    inherit EventRepository()
