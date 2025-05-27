namespace SI.Rosetta.Projections.TestKit

open System.Threading.Tasks
open SI.Rosetta.Projections

type StubCheckpointWriter() =
    interface ICheckpointWriter with
        member this.Write(checkpoint: Checkpoint): Task = 
            Task.CompletedTask