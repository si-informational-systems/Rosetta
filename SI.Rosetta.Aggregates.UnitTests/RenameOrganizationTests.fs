namespace SI.Rosetta.Aggregates.UnitTests

open System
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.TestKit
open System.Collections.Generic
open SI.Rosetta.Common

type RenameOrganizationTests() =
    inherit TestKitBase<OrganizationAggregateHandler>()

    [<Fact>]
    member this.``Should rename Organization``() = 
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
            
            this.Init(id)

            this.Given([|
                OrganizationEvents.Registered { Id = id; Name = "Test Org" } :> IAggregateEvents
            |])

            this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Org Name" })

            let expectedEvent = OrganizationEvents.NameChanged { Id = id; Name = "New Org Name" }
            do! this.Then([| expectedEvent :> IAggregateEvents |])
        }

    [<Fact>]
    member this.``Rename is idempotent``() = 
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
                        
            this.Init(id)

            this.Given([|
                OrganizationEvents.Registered { Id = id; Name = "Test Org" } :> IAggregateEvents;
                OrganizationEvents.NameChanged { Id = id; Name = "New Org Name"}
            |])

            this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Org Name" })

            do! this.ThenNoEvents()
        }

    [<Fact>]
    member this.``Should throw on non-existent Organization``() = 
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
            
            this.Init(id)

            this.Given()

            this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Name" })

            do! this.ExpectError "OrganizationDoesNotExist"
        }

