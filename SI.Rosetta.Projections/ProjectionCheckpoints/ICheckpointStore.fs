namespace SI.Rosetta.Projections

open System.Threading.Tasks

type ICheckpointStore =
    abstract member ReadAsync: id: string -> Task<Checkpoint> 
    abstract member StoreAsync: checkpoint: Checkpoint -> Task 