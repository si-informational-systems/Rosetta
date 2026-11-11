namespace SI.Rosetta.Projections.TestKit

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading.Tasks
open SI.Rosetta.Projections

/// An event's changes to the in-memory store (Some document stored, None deleted), which commit gets with the checkpoint
type TestKitProjectionTransaction(commit: IReadOnlyDictionary<string, obj option> -> Checkpoint -> unit) =
    let Changes = Dictionary<string, obj option>()

    member this.Pending = Changes

    interface IProjectionTransaction with
        member this.CommitAsync(checkpoint) =
            commit Changes checkpoint
            Task.CompletedTask

        member this.DisposeAsync() = ValueTask.CompletedTask

/// In memory, with a transaction per event like the real stores': a handler that fails leaves nothing behind. It's
/// both the document store (INoSqlStore) and the SQL store (ISqlStore): handlers only store, load and delete by key, so
/// the same handler logic runs here as on RavenDB, MongoDB or PostgreSQL
type TestKitProjectionStore() =
    // By read model type and key: a key's values as strings, so string, int, int64, Guid, date and composite keys all work
    let InMemoryStore = ConcurrentDictionary<string, obj>()

    let KeyOf (readModelType: Type) (values: obj[]) =
        values |> Array.iter ValidateIdType
        readModelType.FullName + "\u001F" + String.Join("\u001F", values |> Array.map string)

    let ApplyChanges (changes: IReadOnlyDictionary<string, obj option>) =
        for change in changes do
            match change.Value with
            | Some doc -> InMemoryStore.[change.Key] <- doc
            | None -> InMemoryStore.TryRemove change.Key |> ignore

    /// The event's changes, or None outside a Projection's handlers
    static member private Pending =
        match ProjectionTransaction.Current with
        | :? TestKitProjectionTransaction as transaction -> Some transaction.Pending
        | _ -> None

    /// Applies an event's changes: what a TestKitProjectionTransaction does when it commits
    member this.Apply(changes: IReadOnlyDictionary<string, obj option>) = ApplyChanges changes

    member private this.Load<'T> (id: obj) : 'T =
        let key = KeyOf typeof<'T> (ProjectionKeys.ValuesOf id)
        let found =
            match TestKitProjectionStore.Pending with
            | Some pending when pending.ContainsKey key -> pending.[key]
            | _ ->
                match InMemoryStore.TryGetValue key with
                | true, doc -> Some doc
                | false, _ -> None
        match found with
        | Some doc -> doc :?> 'T
        | None -> Unchecked.defaultof<'T>

    member private this.Write (key: string) (doc: obj option) =
        match TestKitProjectionStore.Pending with
        | Some pending -> pending.[key] <- doc
        | None -> ApplyChanges (dict [ key, doc ] |> Dictionary :> IReadOnlyDictionary<_, _>)

    member private this.Store<'T> (doc: 'T) =
        this.Write (KeyOf typeof<'T> (ProjectionKeys.KeyOf (box doc))) (Some (box doc))

    member private this.Delete<'T> (id: obj) =
        this.Write (KeyOf typeof<'T> (ProjectionKeys.ValuesOf id)) None

    interface IProjectionsStore with
        member this.StoreAsync<'T when 'T : not struct>(doc: 'T) =
            this.Store doc
            Task.CompletedTask

        member this.StoreManyAsync<'T when 'T : not struct>(docs: 'T[]) =
            for doc in docs do
                this.Store doc
            Task.CompletedTask

        member this.LoadAsync<'T when 'T : not struct>(id: obj) =
            Task.FromResult(this.Load<'T> id)

        member this.LoadManyAsync<'T when 'T : not struct>(ids: obj[]) =
            let dict = Dictionary<obj, 'T>()
            for id in ids do
                dict.Add(id, this.Load<'T> id)
            Task.FromResult dict

        member this.DeleteAsync<'T when 'T : not struct>(id: obj) =
            this.Delete<'T> id
            Task.CompletedTask

        member this.DeleteManyAsync<'T when 'T : not struct>(ids: obj[]) =
            for id in ids do
                this.Delete<'T> id
            Task.CompletedTask

    interface INoSqlStore
    interface ISqlStore

    interface IProjectionsUnitOfWork with
        // Any projection, [<ClusterWideTransaction>] too: the TestKit runs its handlers in memory
        member this.PrepareAsync(_projectionType, _readModels) = Task.CompletedTask

        member this.BeginAsync(_projectionType) =
            // The checkpoint isn't kept: the TestKit replays its Given events from the start
            Task.FromResult(new TestKitProjectionTransaction(fun changes _ -> ApplyChanges changes) :> IProjectionTransaction)

        // The TestKit replays its Given events into an empty store
        member this.ClearAsync(_readModels) = Task.CompletedTask
