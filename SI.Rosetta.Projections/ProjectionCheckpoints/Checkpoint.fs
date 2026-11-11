namespace SI.Rosetta.Projections

type Checkpoint() =
    member val Id = "" with get, set
    member val Value = 0UL with get, set
    /// The projection's [<ProjectionVersion>] its read models were built with: 0 without one
    member val Version = 0 with get, set
