namespace SI.Rosetta.Projections.EventStore

open EventStore.Client
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open SI.Rosetta.Projections
open SI.Rosetta.Common

type ESSubscriptionFactory(loggerFactory: ILoggerFactory, configuration: IConfiguration) =
    let client =
        let settings = EventStoreClientSettings.Create(configuration.GetSection("EventStoreDB:ConnectionString").Value)
        new EventStoreClient(settings)

    interface ISubscriptionFactory with
        member this.Create<'TEvent when 'TEvent :> IEvents>() =
            new ESSubscription<'TEvent>(loggerFactory.CreateLogger<ESSubscription<'TEvent>>(), client) :> ISubscription<'TEvent>