namespace SI.Rosetta.Projections.RavenDB

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open Raven.Client.Documents
open Raven.Client.Documents.Operations
open Raven.Client.Documents.Queries
open Raven.Client.Documents.Session
open SI.Rosetta.Projections

/// An event's transaction in RavenDB: one session, saved with the checkpoint in one SaveChanges (one RavenDB
/// transaction). Disposed without committing, nothing is saved.
///
/// Cluster-wide ([<ClusterWideTransaction>]), every document it writes gets an atomic guard, and a write passes only
/// over the version of the document the session loaded: a document it didn't load is taken as new, which fails when
/// the Id has a guard (the document exists, or was deleted outside a cluster-wide transaction, as a rebuild's clear
/// does). So it loads each document before writing it, writes a different object's values onto the loaded one, keeps
/// deletes until the commit (so the Id can be stored again in the event), and updates the stored checkpoint
type RavenDbProjectionTransaction(session: IAsyncDocumentSession, clusterWide: bool) =
    static let FieldsByType = ConcurrentDictionary<Type, FieldInfo[]>()

    /// Every instance field of the type and its bases (an F# record's too)
    static let FieldsOf (documentType: Type) =
        FieldsByType.GetOrAdd(documentType, fun t ->
            Seq.unfold (fun (t: Type) -> if isNull t || t = typeof<obj> then None else Some(t, t.BaseType)) t
            |> Seq.collect (fun t -> t.GetFields(BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.DeclaredOnly))
            |> Seq.toArray)

    // Cluster-wide: the documents the event deleted, by Id, deleted at the commit
    let Deleted = Dictionary<string, obj>(StringComparer.OrdinalIgnoreCase)

    do
        // Every load in the event is a request of this one session
        session.Advanced.MaxNumberOfRequestsPerSession <- Int32.MaxValue

    new(session: IAsyncDocumentSession) = new RavenDbProjectionTransaction(session, false)

    member this.Session = session

    /// Whether the session is cluster-wide ([<ClusterWideTransaction>] on the projection)
    member this.ClusterWide = clusterWide

    /// The session of the event being handled, while a projection's handler runs on RavenDB, for what INoSqlStore
    /// can't say (compare-exchange values, metadata): what it does is saved with the event's writes and its checkpoint.
    /// In a cluster-wide one, load a document before changing it, and change the loaded object
    static member Current : IAsyncDocumentSession option =
        match ProjectionTransaction.Current with
        | :? RavenDbProjectionTransaction as transaction -> Some transaction.Session
        | _ -> None

    /// The document's Id: its Id property or field as a string, or null when it has none
    static member IdOf(document: obj) : string =
        let documentType = document.GetType()
        let id =
            match documentType.GetProperty("Id"), documentType.GetField("Id") with
            | null, null -> null
            | null, field -> field.GetValue(document)
            | property, _ -> property.GetValue(document)
        match id with
        | null -> null
        | id ->
            match id.ToString() with
            | "" -> null
            | text -> text

    /// Writes the document's values onto the object the session loaded for its Id
    static member private CopyOnto(target: obj, source: obj, id: string) =
        if target.GetType() <> source.GetType() then
            raise (InvalidOperationException(
                sprintf "Document %s is a %s in the event's cluster-wide session: a %s can't be stored under its Id." id (target.GetType().Name) (source.GetType().Name)))
        for field in FieldsOf (source.GetType()) do
            field.SetValue(target, field.GetValue(source))

    /// The document, or null when it's missing or the event deleted it
    member this.LoadAsync<'T>(id: string) : Task<'T> =
        if clusterWide && Deleted.ContainsKey id then Task.FromResult Unchecked.defaultof<'T>
        else session.LoadAsync<'T>(id)

    /// The documents by Id (null when missing, or deleted in the event)
    member this.LoadManyAsync<'T>(ids: string[]) : Task<Dictionary<string, 'T>> =
        task {
            let! loaded = session.LoadAsync<'T>(ids).ConfigureAwait(false)
            let result = Dictionary<string, 'T>(loaded)
            if clusterWide then
                for id in ids do
                    if Deleted.ContainsKey id then
                        result.[id] <- Unchecked.defaultof<'T>
            return result
        }

    /// Cluster-wide: loads, in one request, the documents the session doesn't know yet, so writing them passes their guards
    member this.PreloadAsync<'T>(ids: string seq) : Task =
        task {
            if clusterWide then
                let unknown =
                    ids
                    |> Seq.filter (fun id -> not (isNull id) && not (Deleted.ContainsKey id) && not (session.Advanced.IsLoaded id))
                    |> Seq.distinct
                    |> Seq.toArray
                if unknown.Length > 0 then
                    let! _ = session.LoadAsync<'T>(unknown).ConfigureAwait(false)
                    ()
        }

    /// Stores the document. A different object under an Id the session tracks (an F# record copied with `with`, or a
    /// document stored again after its delete) replaces the tracked one, instead of failing as RavenDB would. In a
    /// cluster-wide session, its values are written onto the loaded object instead, which keeps the document's guard
    member this.StoreAsync<'T>(doc: 'T) : Task =
        task {
            match RavenDbProjectionTransaction.IdOf doc with
            | null -> do! session.StoreAsync(doc).ConfigureAwait(false)
            | id when clusterWide ->
                let! current =
                    match Deleted.TryGetValue id with
                    | true, deleted ->
                        Deleted.Remove id |> ignore
                        Task.FromResult deleted
                    | _ ->
                        task {
                            let! loaded = session.LoadAsync<'T>(id).ConfigureAwait(false)
                            return box loaded
                        }
                match current with
                | null -> do! session.StoreAsync(doc, id).ConfigureAwait(false)
                | current when obj.ReferenceEquals(current, doc) -> ()
                | current -> RavenDbProjectionTransaction.CopyOnto(current, doc, id)
            | id ->
                match session.Advanced.GetTrackedEntities().TryGetValue id with
                // A load that found nothing is tracked too, without an entity: nothing to replace then
                | true, tracked when not (isNull tracked.Entity) && not (obj.ReferenceEquals(tracked.Entity, doc)) ->
                    session.Advanced.Evict(tracked.Entity)
                | _ -> ()
                do! session.StoreAsync(doc, id).ConfigureAwait(false)
        }

    /// Deletes through the loaded document, never by a deferred delete of the Id, so the Id can be stored again in the
    /// event. Cluster-wide, the delete waits for the commit, and storing the Id again cancels it
    member this.DeleteAsync<'T>(id: string) : Task =
        task {
            let! document = this.LoadAsync<'T>(id).ConfigureAwait(false)
            if not (isNull (box document)) then
                if clusterWide then Deleted.[id] <- box document
                else session.Delete(document)
        }

    interface IProjectionTransaction with
        member this.CommitAsync(checkpoint: Checkpoint) =
            task {
                if clusterWide then
                    for document in Deleted.Values do
                        session.Delete(document)
                    Deleted.Clear()
                    // The stored checkpoint, updated: a new object would be taken as a new document, which its guard refuses
                    let! stored = session.LoadAsync<Checkpoint>(checkpoint.Id).ConfigureAwait(false)
                    if isNull (box stored) then
                        do! session.StoreAsync(checkpoint, checkpoint.Id).ConfigureAwait(false)
                    else
                        stored.Value <- checkpoint.Value
                        stored.Version <- checkpoint.Version
                else
                    do! session.StoreAsync(checkpoint).ConfigureAwait(false)
                do! session.SaveChangesAsync().ConfigureAwait(false)
            } :> Task

        member this.DisposeAsync() =
            session.Dispose()
            ValueTask.CompletedTask

/// One RavenDB session per event, cluster-wide for a projection with [<ClusterWideTransaction>]
type RavenDbProjectionsUnitOfWork(store: IDocumentStore) =
    /// A document saved on a change vector that another write changed meanwhile, or (cluster-wide) a document or a
    /// compare-exchange value changed since the session read it: a ClusterTransactionConcurrencyException is one too
    interface IRecognizeConflicts with
        member this.IsConflict(ex) = ex :? Raven.Client.Exceptions.ConcurrencyException

    interface IProjectionsUnitOfWork with
        member this.PrepareAsync(_projectionType, _readModels) = Task.CompletedTask

        member this.BeginAsync(projectionType) =
            let clusterWide = ClusterWideTransaction.IsOn projectionType
            let session =
                if clusterWide then store.OpenAsyncSession(SessionOptions(TransactionMode = TransactionMode.ClusterWide))
                else store.OpenAsyncSession()
            Task.FromResult(new RavenDbProjectionTransaction(session, clusterWide) :> IProjectionTransaction)

        /// Each read model's collection, as RavenDB names it. Deleting by query leaves the atomic guards of the documents
        /// a cluster-wide transaction wrote: the replay writes them again after loading them, which passes those guards
        member this.ClearAsync(readModels: Type[]) =
            task {
                for readModel in readModels do
                    let collection = store.Conventions.GetCollectionName(readModel)
                    let query = IndexQuery(Query = sprintf "from '%s'" (collection.Replace("'", "\\'")))
                    let! operation = store.Operations.SendAsync(DeleteByQueryOperation(query)).ConfigureAwait(false)
                    let! _ = operation.WaitForCompletionAsync(TimeSpan.FromMinutes 5.0).ConfigureAwait(false)
                    ()
            } :> Task
