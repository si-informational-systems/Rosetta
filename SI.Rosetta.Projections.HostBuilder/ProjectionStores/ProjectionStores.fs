namespace SI.Rosetta.Projections.HostBuilder

// The stores Projections can write to. The set is closed: the base can't be inherited outside Rosetta (internal
// constructor), the stores can't be inherited at all (sealed), and UseProjectionsWith/AddProjectionsWith require a
// public parameterless constructor, which the base doesn't have. So only these types compile as the store argument

/// The read model stores
type ProjectionStore internal () = class end

[<Sealed>]
type MongoDB() =
    inherit ProjectionStore()

[<Sealed>]
type RavenDB() =
    inherit ProjectionStore()
