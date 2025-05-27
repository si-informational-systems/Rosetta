namespace SI.Rosetta.Projections.TestKit

open System.Collections.Concurrent
open System.Collections.Generic
open SI.Rosetta.Projections

type TestKitProjectionStore() =
    let InMemoryStore = ConcurrentDictionary<string, obj>()
            
    interface IProjectionsStore with
        member _.StoreAsync<'T>(doc: 'T) = task {
            let idProp = doc.GetType().GetProperty("Id")
            let id = idProp.GetValue(doc, null)
            ValidateIdType id
            InMemoryStore.[id.ToString()] <- doc :> obj
        }
            
        member this.StoreInUnitOfWorkAsync<'T>(docs: 'T[]) = task {
            for doc in docs do
                do! (this :> IProjectionsStore).StoreAsync<'T>(doc)
        }
            
        member _.LoadAsync<'T when 'T : not struct>(ids: string[]) = task {
            let dict = Dictionary<string, 'T>()
            for id in ids do
                match InMemoryStore.TryGetValue id with
                | true, value -> dict.Add(id, value :?> 'T)
                | false, _ -> dict.Add(id, null)
            return dict
        }
            
        member _.DeleteAsync(id: string) = task {
            let mutable removed = null
            InMemoryStore.TryRemove(id, &removed) |> ignore
        }
            
        member _.DeleteInUnitOfWorkAsync(ids: string[]) = task {
            for id in ids do
                let mutable removed = null
                InMemoryStore.TryRemove(id, &removed) |> ignore
        }
            
    interface INoSqlStore
    interface ISqlStore