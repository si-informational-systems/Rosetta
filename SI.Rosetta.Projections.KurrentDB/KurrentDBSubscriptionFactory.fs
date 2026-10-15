namespace SI.Rosetta.Projections.KurrentDB

open System.Collections.Generic
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open SI.Rosetta.Projections
open SI.Rosetta.Common
open SI.Rosetta.Persistence.KurrentDB

type KurrentDBSubscriptionFactory(loggerFactory: ILoggerFactory, configuration: IConfiguration, upcasters: IEnumerable<IEventUpcaster>) =
    interface ISubscriptionFactory with
        member this.Create<'TEvent when 'TEvent :> IAggregateEvents>() =
            let client = KurrentDBFactory.InitializeKurrentDB configuration
            new KurrentDBSubscription<'TEvent>(loggerFactory.CreateLogger<KurrentDBSubscription<'TEvent>>(), client, upcasters) :> ISubscription<'TEvent>
