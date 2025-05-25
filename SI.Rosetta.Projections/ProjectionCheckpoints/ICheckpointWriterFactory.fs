namespace SI.Rosetta.Projections

open System.Threading.Tasks

type ICheckpointWriterFactory =
    abstract member Create: unit -> Task<ICheckpointWriter> 