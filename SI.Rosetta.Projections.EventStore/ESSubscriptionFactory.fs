namespace SI.Rosetta.Projections.EventStore

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open SI.Rosetta.Projections
open SI.Rosetta.Common
open SI.Rosetta.Persistence.EventStore

type ESSubscriptionFactory(loggerFactory: ILoggerFactory, configuration: IConfiguration) =
    interface ISubscriptionFactory with
        member this.Create<'TEvent when 'TEvent :> IAggregateEvents>() =
            let client = EventStoreFactory.InitializeEventStore configuration
            new ESSubscription<'TEvent>(loggerFactory.CreateLogger<ESSubscription<'TEvent>>(), client) :> ISubscription<'TEvent>