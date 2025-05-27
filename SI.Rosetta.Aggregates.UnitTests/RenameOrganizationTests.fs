namespace SI.Rosetta.Aggregates.UnitTests

open System
open Xunit
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

            let dict = Dictionary<string,string>()
            dict.Add ("A", "Val")
            let nested : Nested = {
                Value = "Mladen"
                Options = dict
            }
            this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Org Name"; Nested = nested })
            
            let dict2 = Dictionary<string,string>()
            dict2.Add ("A", "Val")
            let nested2 : Nested = {
                Value = "Mladen"
                Options = dict2
            }
            let expectedEvent = OrganizationEvents.NameChanged { Id = id; Name = "New Org Name"; Nested = nested2 }
            do! this.Then([| expectedEvent :> IAggregateEvents |])
        }

    //[<Fact>]
    //member this.``Rename is idempotent``() = 
    //    task {
    //        let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
                        
    //        this.Init(id)

    //        this.Given([|
    //            OrganizationEvents.Registered { Id = id; Name = "Test Org" } :> IAggregateEvents;
    //            OrganizationEvents.NameChanged { Id = id; Name = "New Org Name"}
    //        |])

    //        this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Org Name" })

    //        do! this.ThenNoEvents()
    //    }

    //[<Fact>]
    //member this.``Should throw on non-existent Organization``() = 
    //    task {
    //        let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
            
    //        this.Init(id)

    //        this.Given()

    //        this.When(OrganizationCommands.ChangeName { Id = id; Name = "New Name" })

    //        do! this.ThenError "OrganizationDoesNotExist"
    //    }

