namespace SI.Rosetta.Projections.TestKit

open System.Collections.Concurrent
open System.Collections.Generic
open SI.Rosetta.Projections

type TestKitProjectionStore() =
    let InMemoryStore = ConcurrentDictionary<obj, obj>()
            
    interface IProjectionsStore with
        member _.StoreAsync<'T when 'T : not struct>(doc: 'T) = task {
            let idProp = doc.GetType().GetProperty("Id")
            let id = idProp.GetValue(doc, null)
            ValidateIdType id
            InMemoryStore.[id.ToString()] <- doc :> obj
        }
            
        member this.StoreInUnitOfWorkAsync<'T when 'T : not struct>(docs: 'T[]) = task {
            for doc in docs do
                do! (this :> IProjectionsStore).StoreAsync<'T>(doc).ConfigureAwait(false)
        }

        member this.LoadAsync<'T when 'T : not struct>(id: obj) = 
             task {
                match InMemoryStore.TryGetValue(id) with
                | _, obj -> return obj :?> 'T
            }
            
        member this.LoadManyAsync<'T when 'T : not struct>(ids: obj[]) = 
            task {
                let dict = Dictionary<obj, 'T>()
                for id in ids do
                    match InMemoryStore.TryGetValue id with
                    | true, value -> dict.Add(id, value :?> 'T)
                    | false, _ -> dict.Add(id, null)
                return dict
            }
            
        member _.DeleteAsync(id: obj) = task {
            let mutable removed = null
            InMemoryStore.TryRemove(id, &removed) |> ignore
        }
            
        member _.DeleteInUnitOfWorkAsync(ids: obj[]) = task {
            for id in ids do
                let mutable removed = null
                InMemoryStore.TryRemove(id, &removed) |> ignore
        }
            
    interface INoSqlStore
    interface ISqlStore