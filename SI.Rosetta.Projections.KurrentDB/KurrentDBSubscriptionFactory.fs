namespace SI.Rosetta.Projections.KurrentDB

open System.Collections.Generic
open KurrentDB.Client
open Microsoft.Extensions.Logging
open SI.Rosetta.Projections
open SI.Rosetta.Common

/// Every subscription shares the application's KurrentDBClient (see KurrentDBServices.AddKurrentDBClients), and reports
/// to the application's ProjectionStatuses
type KurrentDBSubscriptionFactory
    (loggerFactory: ILoggerFactory, client: KurrentDBClient, upcasters: IEnumerable<IEventUpcaster>,
     statuses: ProjectionStatuses, retries: ProjectionRetries) =

    /// Without the application's ProjectionStatuses, each factory keeps its own
    new(loggerFactory: ILoggerFactory, client: KurrentDBClient, upcasters: IEnumerable<IEventUpcaster>) =
        KurrentDBSubscriptionFactory(loggerFactory, client, upcasters, ProjectionStatuses(), ProjectionRetries.Default)

    interface ISubscriptionFactory with
        member this.Create<'TEvent when 'TEvent :> IAggregateEvents>() =
            new KurrentDBSubscription<'TEvent>(
                loggerFactory.CreateLogger<KurrentDBSubscription<'TEvent>>(), client, upcasters, statuses, retries) :> ISubscription<'TEvent>
