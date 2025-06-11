namespace SI.Rosetta.Aggregates.UnitTests

open System
open Xunit
open SI.Rosetta.Aggregates.TestKit
open SI.Rosetta.Common

type OrganizationTests() =
    inherit TestKitBase<OrganizationAggregateHandler>()

    [<Fact>]
    member this.``Should Register Organization``() = 
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
            let ev = OrganizationEvents.Registered { Id = id; Name = "Test Org" }
            let expectedProducedEvents = ResizeArray([ev :> IAggregateEvents])
            let expectedPublishedEvents = ResizeArray([ev :> IAggregateEvents])

            this.Given()

            this.When(OrganizationCommands.Register { Id = id; Name = "Test Org" })

            do! this.Then(expectedProducedEvents, expectedPublishedEvents).ConfigureAwait(false)
        }

    [<Fact>]
    member this.``Should Rename Organization``() = 
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
            
            this.Given([|
                OrganizationEvents.Registered { Id = id; Name = "Test Org" } :> IAggregateEvents
            |])

            this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Org Name" })
            
            let expectedEvent = OrganizationEvents.NameChanged { Id = id; Name = "New Org Name" }
            do! this.Then([| expectedEvent :> IAggregateEvents |]).ConfigureAwait(false)
        }

    [<Fact>]
    member this.``Rename is idempotent``() = 
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())

            this.Given([|
                OrganizationEvents.Registered { Id = id; Name = "Test Org" } :> IAggregateEvents;
                OrganizationEvents.NameChanged { Id = id; Name = "New Org Name"}
            |])

            this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Org Name" })

            do! this.ThenNoEvents().ConfigureAwait(false)
        }

    [<Fact>]
    member this.``Should throw on non-existent Organization``() = 
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())

            this.Given()

            this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Name" })

            do! this.ThenError("OrganizationDoesNotExist").ConfigureAwait(false)
        }

