namespace SI.Rosetta.Projections.HostBuilder

type ProjectionStore() = class end

type MongoDB() = 
    inherit ProjectionStore()
    
type RavenDB() = 
    inherit ProjectionStore()
