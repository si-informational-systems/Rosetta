namespace SI.Rosetta.Projections.HostBuilder

type ProjectionStore() = class end

type Mongo() = 
    inherit ProjectionStore()
    
type Raven() = 
    inherit ProjectionStore()
