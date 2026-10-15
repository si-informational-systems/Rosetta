namespace SI.Rosetta.Projections.TestKit

open System.Collections.Concurrent
open System.Collections.Generic
open SI.Rosetta.Projections

type TestKitProjectionStore() =
    // Keyed by the Id's string, as the RavenDB and MongoDB stores do, so string, int, int64 and Guid Ids all work
    let InMemoryStore = ConcurrentDictionary<string, obj>()
    let Key (id: obj) = id.ToString()

    member private this.Load<'T> (id: obj) : 'T =
        match InMemoryStore.TryGetValue(Key id) with
        | true, doc -> doc :?> 'T
        | false, _ -> Unchecked.defaultof<'T>

    interface IProjectionsStore with
        member this.StoreAsync<'T when 'T : not struct>(doc: 'T) = task {
            let idProp = doc.GetType().GetProperty("Id")
            let id = idProp.GetValue(doc, null)
            ValidateIdType id
            InMemoryStore.[Key id] <- doc :> obj
        }

        member this.StoreInUnitOfWorkAsync<'T when 'T : not struct>(docs: 'T[]) = task {
            for doc in docs do
                do! (this :> IProjectionsStore).StoreAsync<'T>(doc).ConfigureAwait(false)
        }

        member this.LoadAsync<'T when 'T : not struct>(id: obj) =
            task {
                return this.Load<'T> id
            }

        member this.LoadManyAsync<'T when 'T : not struct>(ids: obj[]) =
            task {
                let dict = Dictionary<obj, 'T>()
                for id in ids do
                    dict.Add(id, this.Load<'T> id)
                return dict
            }

        member this.DeleteAsync<'T when 'T : not struct>(id: obj) = task {
            InMemoryStore.TryRemove(Key id) |> ignore
        }

        member this.DeleteInUnitOfWorkAsync<'T when 'T : not struct>(ids: obj[]) = task {
            for id in ids do
                InMemoryStore.TryRemove(Key id) |> ignore
        }

    interface INoSqlStore
    interface ISqlStore
