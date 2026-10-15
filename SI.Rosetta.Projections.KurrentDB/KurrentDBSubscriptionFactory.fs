namespace SI.Rosetta.Projections.KurrentDB

open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open SI.Rosetta.Projections
open SI.Rosetta.Common
open SI.Rosetta.Persistence.KurrentDB

type KurrentDBSubscriptionFactory(loggerFactory: ILoggerFactory, configuration: IConfiguration) =
    interface ISubscriptionFactory with
        member this.Create<'TEvent when 'TEvent :> IAggregateEvents>() =
            let client = KurrentDBFactory.InitializeKurrentDB configuration
            new KurrentDBSubscription<'TEvent>(loggerFactory.CreateLogger<KurrentDBSubscription<'TEvent>>(), client) :> ISubscription<'TEvent>