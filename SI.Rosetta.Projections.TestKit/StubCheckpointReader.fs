namespace SI.Rosetta.Projections.TestKit

open System.Threading.Tasks
open SI.Rosetta.Projections

type StubCheckpointReader() =
    interface ICheckpointReader with
        member this.Read(id: string): Task<Checkpoint> =
            let c = Checkpoint(Id = id, Value = 0UL)
            Task.FromResult(c)