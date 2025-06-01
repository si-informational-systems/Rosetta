namespace SI.Rosetta.Aggregates.UnitTests

open System
open Xunit
open SI.Rosetta.Aggregates.TestKit
open SI.Rosetta.Common

type RegisterOrganizationTests() =
    inherit TestKitBase<OrganizationAggregateHandler>()

    [<Fact>]
    member this.``Should Open Wallet``() = 
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
            let ev = OrganizationEvents.Registered { Id = id; Name = "Test Org" }
            let expectedProducedEvents = ResizeArray([ev :> IAggregateEvents])
            let expectedPublishedEvents = expectedProducedEvents

            this.Given()

            this.When(OrganizationCommands.Register { Id = id; Name = "Test Org" })

            do! this.Then(expectedProducedEvents, expectedPublishedEvents).ConfigureAwait(false)
        }

