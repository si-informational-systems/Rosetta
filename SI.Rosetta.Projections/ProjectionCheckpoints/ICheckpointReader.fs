namespace SI.Rosetta.Projections

open System.Threading.Tasks

type ICheckpointReader =
    abstract member ReadAsync: id: string -> Task<Checkpoint> 