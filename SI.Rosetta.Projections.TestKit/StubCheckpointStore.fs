namespace SI.Rosetta.Projections.TestKit

open System.Threading.Tasks
open SI.Rosetta.Projections

type StubCheckpointStore() =
    interface ICheckpointStore with
        member this.ReadAsync(id: string): Task<Checkpoint> =
            let c = Checkpoint(Id = id, Value = 0UL)
            Task.FromResult(c)
        member this.StoreAsync(checkpoint: Checkpoint): Task = 
            Task.CompletedTask