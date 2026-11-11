namespace SI.Rosetta.Aggregates.RavenDB

open System
open System.Threading
open System.Threading.Tasks
open Raven.Client
open Raven.Client.Documents
open Raven.Client.Documents.Operations.Expiration
open Raven.Client.Documents.Session
open Raven.Client.Exceptions.Commercial
open Raven.Client.Exceptions.Security
open Raven.Client.ServerWide.Operations
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
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
      TraceState: string
      /// When the outbox's publisher published it; null until then
      PublishedAt: Nullable<DateTime> }

/// The lease of the outbox's publisher: one instance of the app publishes at a time
[<CLIMutable>]
type RavenDBOutboxLease = { Id: string; Owner: string; ExpiresAt: DateTime }

/// Records the events as {CollectionName}/{Id} documents of the CollectionName collection, all of one command's in one
/// transaction, skipping the ones it already holds. With a TTL, each document expires through its @expires metadata,
/// so the TTL is in the documents and changing it changes only the events recorded from then on. With a publisher, the
/// @expires is set when the event is published. The publisher's lease is OutboxLeases/{CollectionName}
type RavenDBOutbox(store: IDocumentStore, options: OutboxOptions, logger: ILogger<RavenDBOutbox>) =
    let CollectionName = options.CollectionName
    let EventTTL = Option.ofNullable options.EventTTL
    let ExpirationFrequency = options.ExpirationFrequency
    let mutable ExpirationChecked = EventTTL.IsNone
    let CheckingExpiration = new SemaphoreSlim(1, 1)
    let LeaseId = sprintf "OutboxLeases/%s" CollectionName

    let DocumentIdOf (event: OutboxEvent) = sprintf "%s/%O" CollectionName event.Id

    let EventOf (document: RavenDBOutboxEvent) : OutboxEvent =
        { Id = Guid.Parse(document.Id.Substring(CollectionName.Length + 1))
          Aggregate = document.Aggregate
          AggregateId = document.AggregateId
          Version = document.Version
          Position = document.Position
          EventType = document.EventType
          Data = document.Data
          RecordedAt = DateTime.SpecifyKind(document.RecordedAt, DateTimeKind.Utc)
          TraceParent = document.TraceParent
          TraceState = document.TraceState }

    /// Without a logger: a licence that refuses the expiration frequency goes unreported
    new(store: IDocumentStore, options: OutboxOptions) = RavenDBOutbox(store, options, NullLogger<RavenDBOutbox>.Instance)

    /// Turns the database's document expiration on, once, when it's off: it's what deletes the expired events. It
    /// deletes them every ExpirationFrequency (36 hours unless set, which every licence allows). A certificate that
    /// can't read the database record, or a licence that refuses the frequency, leaves it as it is
    member private this.EnsureExpirationAsync() =
        task {
            if not ExpirationChecked then
                do! CheckingExpiration.WaitAsync().ConfigureAwait(false)
                try
                    if not ExpirationChecked then
                        try
                            let! record = store.Maintenance.Server.SendAsync(GetDatabaseRecordOperation(store.Database)).ConfigureAwait(false)
                            if isNull record.Expiration || record.Expiration.Disabled then
                                let configuration =
                                    ExpirationConfiguration(Disabled = false, DeleteFrequencyInSec = Nullable(int64 ExpirationFrequency.TotalSeconds))
                                let! _ = store.Maintenance.SendAsync(ConfigureExpirationOperation(configuration)).ConfigureAwait(false)
                                ()
                        with
                        | :? AuthorizationException -> ()
                        | :? LicenseLimitException as refused ->
                            logger.LogWarning(
                                refused,
                                "The outbox couldn't turn document expiration on for RavenDB database {Database}, deleting every {ExpirationFrequency}: the licence refused it. "
                                + "Its expired events won't be deleted until document expiration is turned on by hand (Studio: Settings → Document Expiration), "
                                + "with a frequency the licence allows.",
                                store.Database, ExpirationFrequency)
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
                      TraceState = event.TraceState
                      PublishedAt = Nullable() }
                // An empty change vector: saved only while no document has the id
                do! session.StoreAsync(document, String.Empty, document.Id).ConfigureAwait(false)
                let metadata = session.Advanced.GetMetadataFor(document)
                metadata.[Constants.Documents.Metadata.Collection] <- box CollectionName
                // Waiting to be published, it doesn't expire
                if not options.ExpiresFromPublication then
                    EventTTL |> Option.iter (fun ttl ->
                        metadata.[Constants.Documents.Metadata.Expires] <- box (event.RecordedAt.Add ttl))
            return missing
        }

    /// The lease, taken or renewed when it's free, expired or already the owner's. Saved on the change vector it was
    /// read with (none: while there's none), so of two instances taking it at once, one fails
    member private this.LeaseAsync(owner: string, duration: TimeSpan) : Task<bool> =
        task {
            use session = store.OpenAsyncSession()
            let! current = session.LoadAsync<RavenDBOutboxLease>(LeaseId).ConfigureAwait(false)
            let now = DateTime.UtcNow
            let free = isNull (box current) || current.Owner = owner || current.ExpiresAt < now
            if not free then return false
            else
                let changeVector = if isNull (box current) then String.Empty else session.Advanced.GetChangeVectorFor(current)
                // Stored as a new object: the session tracks the one it loaded
                if not (isNull (box current)) then session.Advanced.Evict(current)
                let lease = { Id = LeaseId; Owner = owner; ExpiresAt = now.Add duration }
                do! session.StoreAsync(lease, changeVector, LeaseId).ConfigureAwait(false)
                try
                    do! session.SaveChangesAsync().ConfigureAwait(false)
                    return true
                with :? Raven.Client.Exceptions.ConcurrencyException ->
                    return false
        }

    interface IOutbox with
        member this.RecordAsync(events: OutboxEvent[]) =
            task {
                use session = store.OpenAsyncSession()
                let! recorded = this.StoreAsync(session, events).ConfigureAwait(false)
                if recorded.Length > 0 then
                    try
                        do! session.SaveChangesAsync().ConfigureAwait(false)
                    with :? Raven.Client.Exceptions.ConcurrencyException as conflict ->
                        raise (SI.Rosetta.Common.ConcurrencyException(
                                sprintf "The outbox's event %s was recorded by another command meanwhile, so these events weren't: run the command again." conflict.Id,
                                conflict))
                return recorded.Length
            }

    interface IOutboxRelayStore with
        member this.TryLeaseAsync(owner, duration, _cancellationToken) = this.LeaseAsync(owner, duration)

        /// PublishedAt null, not missing: the documents of an outbox that had no publisher before (published by the app
        /// itself) aren't published again. The query waits for the index, so it sees every event recorded before it
        member this.UnpublishedAsync(max, cancellationToken) =
            task {
                use session = store.OpenAsyncSession()
                let! documents =
                    session.Advanced.AsyncDocumentQuery<RavenDBOutboxEvent>(null, CollectionName, false)
                        .WaitForNonStaleResults()
                        .WhereEquals("PublishedAt", null)
                        .OrderBy("RecordedAt", OrderingType.String)
                        .OrderBy("Position", OrderingType.Long)
                        .Take(max)
                        .ToListAsync(cancellationToken).ConfigureAwait(false)
                return documents |> Seq.map EventOf |> Seq.toArray
            }

        member this.MarkPublishedAsync(events, publishedAt, cancellationToken) =
            task {
                if events.Length > 0 then
                    do! this.EnsureExpirationAsync().ConfigureAwait(false)
                    use session = store.OpenAsyncSession()
                    let! documents = session.LoadAsync<RavenDBOutboxEvent>(events |> Array.map DocumentIdOf, cancellationToken).ConfigureAwait(false)
                    for KeyValue(_, document) in documents do
                        if not (isNull (box document)) then
                            // A record is immutable: the published one replaces it
                            session.Advanced.Evict(document)
                            let published = { document with PublishedAt = Nullable publishedAt }
                            do! session.StoreAsync(published, published.Id, cancellationToken).ConfigureAwait(false)
                            let metadata = session.Advanced.GetMetadataFor(published)
                            metadata.[Constants.Documents.Metadata.Collection] <- box CollectionName
                            EventTTL |> Option.iter (fun ttl ->
                                metadata.[Constants.Documents.Metadata.Expires] <- box (publishedAt.Add ttl))
                    do! session.SaveChangesAsync(cancellationToken).ConfigureAwait(false)
            } :> Task
