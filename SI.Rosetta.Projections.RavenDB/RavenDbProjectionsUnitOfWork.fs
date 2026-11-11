namespace SI.Rosetta.Projections.RavenDB

open System
open System.Threading.Tasks
open Raven.Client.Documents
open Raven.Client.Documents.Operations
open Raven.Client.Documents.Queries
open Raven.Client.Documents.Session
open SI.Rosetta.Projections

/// An event's transaction in RavenDB: one session, saved with the checkpoint in one SaveChanges (one RavenDB
/// transaction). Disposed without committing, nothing is saved
type RavenDbProjectionTransaction(session: IAsyncDocumentSession) =
    do
        // Every load in the event is a request of this one session
        session.Advanced.MaxNumberOfRequestsPerSession <- Int32.MaxValue

    member this.Session = session

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

    /// Stores the document. A different object under an Id the session tracks (an F# record copied with `with`, or a
    /// document stored again after its delete) replaces the tracked one, instead of failing as RavenDB would
    member this.StoreAsync(doc: obj) : Task =
        task {
            match RavenDbProjectionTransaction.IdOf doc with
            | null -> do! session.StoreAsync(doc).ConfigureAwait(false)
            | id ->
                match session.Advanced.GetTrackedEntities().TryGetValue id with
                // A load that found nothing is tracked too, without an entity: nothing to replace then
                | true, tracked when not (isNull tracked.Entity) && not (obj.ReferenceEquals(tracked.Entity, doc)) ->
                    session.Advanced.Evict(tracked.Entity)
                | _ -> ()
                do! session.StoreAsync(doc, id).ConfigureAwait(false)
        }

    /// Deletes through the loaded document, never by a deferred delete of the Id, so the Id can be stored again in the event
    member this.DeleteAsync<'T>(id: string) : Task =
        task {
            let! document = session.LoadAsync<'T>(id).ConfigureAwait(false)
            if not (isNull (box document)) then
                session.Delete(document)
        }

    interface IProjectionTransaction with
        member this.CommitAsync(checkpoint: Checkpoint) =
            task {
                do! session.StoreAsync(checkpoint).ConfigureAwait(false)
                do! session.SaveChangesAsync().ConfigureAwait(false)
            } :> Task

        member this.DisposeAsync() =
            session.Dispose()
            ValueTask.CompletedTask

/// One RavenDB session per event
type RavenDbProjectionsUnitOfWork(store: IDocumentStore) =
    interface IProjectionsUnitOfWork with
        member this.PrepareAsync(_readModels) = Task.CompletedTask

        member this.BeginAsync() =
            Task.FromResult(new RavenDbProjectionTransaction(store.OpenAsyncSession()) :> IProjectionTransaction)

        /// Each read model's collection, as RavenDB names it
        member this.ClearAsync(readModels: Type[]) =
            task {
                for readModel in readModels do
                    let collection = store.Conventions.GetCollectionName(readModel)
                    let query = IndexQuery(Query = sprintf "from '%s'" (collection.Replace("'", "\\'")))
                    let! operation = store.Operations.SendAsync(DeleteByQueryOperation(query)).ConfigureAwait(false)
                    let! _ = operation.WaitForCompletionAsync(TimeSpan.FromMinutes 5.0).ConfigureAwait(false)
                    ()
            } :> Task
