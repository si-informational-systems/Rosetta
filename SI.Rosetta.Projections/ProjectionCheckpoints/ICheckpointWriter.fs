namespace SI.Rosetta.Projections

open System.Threading.Tasks

type ICheckpointWriter =
    abstract member Write: checkpoint: Checkpoint -> Task 