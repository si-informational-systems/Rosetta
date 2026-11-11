namespace SI.Rosetta.Aggregates.UnitTests

open System
open System.Threading.Tasks
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.TestKit
open SI.Rosetta.Common

/// The TestKit's repository, losing its first conflicts stores to another command
type RacingRepository(conflicts: int) =
    let Wrapped = TestKitAggregateRepository() :> IAggregateRepository
    let mutable Remaining = conflicts
    member val Loads = 0 with get, set
    member val Stores = 0 with get, set
    member this.Inner = Wrapped

    interface IEventSourcedAggregateRepository
    interface IAggregateRepository with
        member this.StoreAsync<'TAggregateState when 'TAggregateState :> IAggregateState>(aggregate: IAggregate<'TAggregateState>) =
            this.Stores <- this.Stores + 1
            if Remaining > 0 then
                Remaining <- Remaining - 1
                Task.FromException(ConcurrencyException "Another command stored it first")
            else Wrapped.StoreAsync(aggregate)

        member this.GetAsync<'TAggregateState
                                when 'TAggregateState : (new : unit -> 'TAggregateState)
                                and 'TAggregateState :> IAggregateState>(id: string, version: int64) =
            this.Loads <- this.Loads + 1
            Wrapped.GetAsync<'TAggregateState>(id, version)

/// A command that lost to another is retried at once, on the aggregate loaded again, before the caller sees it
type HandlerConflictRetryTests() =
    let Id = sprintf "Organizations-%s" (Guid.NewGuid().ToString("N"))
    let Rename = OrganizationCommands.ChangeName { Id = Id; Name = "Rosetta Stone" }

    /// A registered organization behind a repository that loses its next conflicts stores
    let Handler (conflicts: int) =
        task {
            let repository = RacingRepository(conflicts)
            let registered = OrganizationAggregate()
            (registered :> IAggregateInstance<OrganizationCommands>).Execute(OrganizationCommands.Register { Id = Id; Name = "Rosetta" })
            do! repository.Inner.StoreAsync(registered)
            return repository, OrganizationAggregateHandler(repository)
        }

    [<Fact>]
    member this.``A command that lost to another is loaded and executed again, and goes through``() =
        task {
            let! repository, handler = Handler 2

            do! (handler :> IAggregateHandler).ExecuteAsync(Rename)

            Assert.Equal(3, repository.Stores)
            Assert.Equal(3, repository.Loads)
            let! stored = repository.Inner.GetAsync<OrganizationAggregateState>(Id)
            Assert.Equal("Rosetta Stone", stored.Value.Name)
        }

    [<Fact>]
    member this.``A conflict that outlasts the retries reaches the caller``() =
        task {
            let! repository, handler = Handler 10
            handler.ConflictRetries <- { Retries = 2; MaxJitter = TimeSpan.FromMilliseconds 5.0 }

            let! _ = Assert.ThrowsAsync<ConcurrencyException>(fun () -> (handler :> IAggregateHandler).ExecuteAsync(Rename) :> Task)

            Assert.Equal(3, repository.Stores)
        }

    [<Fact>]
    member this.``Without retries, a conflict reaches the caller at once``() =
        task {
            let! repository, handler = Handler 1
            handler.ConflictRetries <- ConflictRetries.None

            let! _ = Assert.ThrowsAsync<ConcurrencyException>(fun () -> (handler :> IAggregateHandler).ExecuteAsync(Rename) :> Task)

            Assert.Equal(1, repository.Stores)
        }
