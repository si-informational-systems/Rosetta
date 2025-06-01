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
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString())
            let ev = OrganizationEvents.Registered { Id = id; Name = "Test Org"; DateRegistered = datetime }

            do! this.Given(ev).ConfigureAwait(false)

            let doc : Organization = {
                Id = id
                Name = "Test Org"
                DateRegistered = datetime
            }

            do! this.Then(doc).ConfigureAwait(false)
        }