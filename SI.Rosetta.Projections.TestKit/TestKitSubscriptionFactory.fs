namespace SI.Rosetta.Projections.TestKit

open SI.Rosetta.Projections
open SI.Rosetta.Common

type TestKitSubscriptionFactory() =
    interface ISubscriptionFactory with
        member this.Create<'TEvent when 'TEvent :> IAggregateEvents>() =
            TestKitSubscription<'TEvent>()