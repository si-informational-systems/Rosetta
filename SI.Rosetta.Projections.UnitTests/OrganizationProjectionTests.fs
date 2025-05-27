namespace SI.Rosetta.Projections.UnitTests

open System
open Xunit
open SI.Rosetta.Projections.TestKit

type OrganizationProjectionTests() =
    inherit TestKitBase<OrganizationProjection, OrganizationProjectionHandler, OrganizationEvents>()

    [<Fact>]
    member this.``Should register Organization``() =
        task {
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
            let ev = OrganizationEvents.Registered { Id = id; Name = "Test Org" }

            do! this.Given(ev)

            let doc : Organization = {
                Id = id
                Name = "Test Org"
            }

            do! this.Then(doc)
        }