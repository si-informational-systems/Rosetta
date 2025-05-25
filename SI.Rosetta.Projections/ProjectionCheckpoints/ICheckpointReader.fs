namespace SI.Rosetta.Projections

open System.Threading.Tasks

type ICheckpointReader =
    abstract member Read: id: string -> Task<Checkpoint> 