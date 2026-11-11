namespace SI.Rosetta.Aggregates.UnitTests

open System
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.TestKit

/// The TestKit's in-memory repository checks the version an aggregate was loaded at, as every store does
type TestKitRepositoryTests() =
    let Repository = TestKitAggregateRepository() :> IAggregateRepository
    let Id = sprintf "Organizations-%s" (Guid.NewGuid().ToString("N"))

    let Execute (command: OrganizationCommands) (aggregate: OrganizationAggregate) =
        (aggregate :> IAggregateInstance<OrganizationCommands>).Execute command
        aggregate

    let Registered () = OrganizationAggregate() |> Execute (OrganizationCommands.Register { Id = Id; Name = "Rosetta" })

    let Renamed (name: string) =
        task {
            let! state = Repository.GetAsync<OrganizationAggregateState>(Id)
            return OrganizationAggregate(State = state.Value) |> Execute (OrganizationCommands.ChangeName { Id = Id; Name = name })
        }

    [<Fact>]
    member this.``A command that loaded an older version is refused``() =
        task {
            do! Repository.StoreAsync(Registered ())
            let! first = Renamed "Rosetta Stone"
            let! second = Renamed "Rosetta Code"
            do! Repository.StoreAsync(first)

            let! refused = Assert.ThrowsAsync<ConcurrencyException>(fun () -> Repository.StoreAsync(second))

            Assert.Contains("is no longer at version 1", refused.Message)
        }

    [<Fact>]
    member this.``Creating an aggregate that exists is refused``() =
        task {
            do! Repository.StoreAsync(Registered ())

            let! refused = Assert.ThrowsAsync<ConcurrencyException>(fun () -> Repository.StoreAsync(Registered ()))

            Assert.Contains("already exists", refused.Message)
        }
