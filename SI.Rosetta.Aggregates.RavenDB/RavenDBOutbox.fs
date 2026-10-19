namespace SI.Rosetta.Aggregates.RavenDB

open System
open System.Threading
open System.Threading.Tasks
open Raven.Client
open Raven.Client.Documents
open Raven.Client.Documents.Operations.Expiration
open Raven.Client.Documents.Session
open Raven.Client.Exceptions.Security
open Raven.Client.ServerWide.Operations
open SI.Rosetta.Aggregates

/// An outbox event as RavenDB holds it: a document of the outbox collection, expiring through its @expires metadata
[<CLIMutable>]
type RavenDBOutboxEvent =
    { Id: string
      Aggregate: string
      AggregateId: string
      Version: int64
      Position: int
      EventType: string
      /// The event's record as JSON, exactly as KurrentDB stores it
      Data: string
      RecordedAt: DateTime
      TraceParent: string
      TraceState: string }

/// Records the events as {CollectionName}/{Id} documents of the CollectionName collection, all of one command's in one
/// transaction, skipping the ones it already holds. With a TTL, each document expires through its @expires metadata,
/// so the TTL is in the documents and changing it changes only the events recorded from then on
type RavenDBOutbox(store: IDocumentStore, options: OutboxOptions) =
    let CollectionName = options.CollectionName
    let EventTTL = Option.ofNullable options.EventTTL
    let mutable ExpirationChecked = EventTTL.IsNone
    let CheckingExpiration = new SemaphoreSlim(1, 1)

    let DocumentIdOf (event: OutboxEvent) = sprintf "%s/%O" CollectionName event.Id

    /// Turns the database's document expiration on, once, when it's off: it's what deletes the expired events.
    /// A certificate that can't read the database record leaves it as it is
    member private this.EnsureExpirationAsync() =
        task {
            if not ExpirationChecked then
                do! CheckingExpiration.WaitAsync().ConfigureAwait(false)
                try
                    if not ExpirationChecked then
                        try
                            let! record = store.Maintenance.Server.SendAsync(GetDatabaseRecordOperation(store.Database)).ConfigureAwait(false)
                            if isNull record.Expiration || record.Expiration.Disabled then
                                let! _ = store.Maintenance.SendAsync(ConfigureExpirationOperation(ExpirationConfiguration(Disabled = false))).ConfigureAwait(false)
                                ()
                        with :? AuthorizationException -> ()
                        ExpirationChecked <- true
                finally
                    CheckingExpiration.Release() |> ignore
        }

    /// The events the outbox doesn't hold yet. Looked up in a session of their own: the session they're stored in
    /// would track the ones it loads, and could save them again
    member private this.MissingAsync(events: OutboxEvent[]) =
        task {
            use lookup = store.OpenAsyncSession()
            let! held = lookup.LoadAsync<RavenDBOutboxEvent>(events |> Array.map DocumentIdOf).ConfigureAwait(false)
            return
                events |> Array.filter (fun event ->
                    match held.TryGetValue(DocumentIdOf event) with
                    | true, document -> isNull (box document)
                    | false, _ -> true)
        }

    /// Stores the events the outbox doesn't hold yet in the session, so they're saved, in one transaction, with whatever
    /// else it saves, and returns them. Each must still not exist when it's saved: one a concurrent repeat of the
    /// command recorded meanwhile fails the save with a ConcurrencyException, instead of being overwritten
    member this.StoreAsync(session: IAsyncDocumentSession, events: OutboxEvent[]) : Task<OutboxEvent[]> =
        task {
            do! this.EnsureExpirationAsync().ConfigureAwait(false)
            let! missing = this.MissingAsync(events).ConfigureAwait(false)
            for event in missing do
                let document =
                    { Id = DocumentIdOf event
                      Aggregate = event.Aggregate
                      AggregateId = event.AggregateId
                      Version = event.Version
                      Position = event.Position
                      EventType = event.EventType
                      Data = event.Data
                      RecordedAt = event.RecordedAt
                      TraceParent = event.TraceParent
                      TraceState = event.TraceState }
                // An empty change vector: saved only while no document has the id
                do! session.StoreAsync(document, String.Empty, document.Id).ConfigureAwait(false)
                let metadata = session.Advanced.GetMetadataFor(document)
                metadata.[Constants.Documents.Metadata.Collection] <- box CollectionName
                EventTTL |> Option.iter (fun ttl ->
                    metadata.[Constants.Documents.Metadata.Expires] <- box (event.RecordedAt.Add ttl))
            return missing
        }

    interface IOutbox with
        member this.RecordAsync(events: OutboxEvent[]) =
            task {
                use session = store.OpenAsyncSession()
                let! recorded = this.StoreAsync(session, events).ConfigureAwait(false)
                if recorded.Length > 0 then
                    do! session.SaveChangesAsync().ConfigureAwait(false)
                return recorded.Length
            }
