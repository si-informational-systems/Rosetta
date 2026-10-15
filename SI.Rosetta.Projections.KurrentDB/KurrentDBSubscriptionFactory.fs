namespace SI.Rosetta.Projections.KurrentDB

open System.Collections.Generic
open KurrentDB.Client
open Microsoft.Extensions.Logging
open SI.Rosetta.Projections
open SI.Rosetta.Common

/// Every subscription shares the application's KurrentDBClient (see KurrentDBServices.AddKurrentDBClients)
type KurrentDBSubscriptionFactory(loggerFactory: ILoggerFactory, client: KurrentDBClient, upcasters: IEnumerable<IEventUpcaster>) =
    interface ISubscriptionFactory with
        member this.Create<'TEvent when 'TEvent :> IAggregateEvents>() =
            new KurrentDBSubscription<'TEvent>(loggerFactory.CreateLogger<KurrentDBSubscription<'TEvent>>(), client, upcasters) :> ISubscription<'TEvent>
