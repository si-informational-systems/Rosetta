namespace SI.Rosetta.Projections.UnitTests

open System
open Xunit
open SI.Rosetta.Projections.TestKit

type OrganizationProjectionTests() =
    inherit TestKitBase<OrganizationProjection, OrganizationProjectionHandler, OrganizationEvents>()

    [<Fact>]
    member this.``Should register Organization``() =
        task {
            let datetime = DateTime.MinValue
            let id = "Organizations-1"
            let ev = OrganizationEvents.Registered { Id = id; Name = "Test Org"; DateRegistered = datetime }

            let nameChanged = OrganizationEvents.NameChanged { Id = id; Name = "Test Org 2" }

            do! this.Given(ev, nameChanged).ConfigureAwait(false)

            let doc : Organization = {
                Id = id
                Name = "Test Org 2"
                DateRegistered = datetime
            }

            do! this.Then(doc).ConfigureAwait(false)
        }