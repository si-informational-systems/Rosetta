namespace SI.Rosetta.Projections.UnitTests

open System
open System.ComponentModel.DataAnnotations
open System.Threading.Tasks
open Xunit
open SI.Rosetta.Projections
open SI.Rosetta.Projections.TestKit

/// A row with a composite key: a reservation's hour in an area
[<CLIMutable>]
type AreaHour = { [<Key>] AreaId: string; [<Key>] HourStart: DateTime; Guests: int }

type SqlOrganizationHandler(store: ISqlStore) =
    interface IProjectionHandler<OrganizationEvents> with
        member _.Handle(_, _) = Task.CompletedTask

type DocumentOrganizationHandler(store: INoSqlStore) =
    interface IProjectionHandler<OrganizationEvents> with
        member _.Handle(_, _) = Task.CompletedTask

type NoStoreOrganizationHandler() =
    interface IProjectionHandler<OrganizationEvents> with
        member _.Handle(_, _) = Task.CompletedTask

[<HandlesStream(ProjectionStreamNames.OrganizationStream); DisabledProjection>]
type SqlOrganizationProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<SqlOrganizationHandler, OrganizationEvents>
    interface IAmHandledBy<NoStoreOrganizationHandler, OrganizationEvents>

[<HandlesStream(ProjectionStreamNames.OrganizationStream); DisabledProjection>]
type NoStoreOrganizationProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<NoStoreOrganizationHandler, OrganizationEvents>

[<HandlesStream(ProjectionStreamNames.OrganizationStream); DisabledProjection>]
type MixedOrganizationProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<SqlOrganizationHandler, OrganizationEvents>
    interface IAmHandledBy<DocumentOrganizationHandler, OrganizationEvents>

/// Which store a projection writes to, and the read models' keys
type ProjectionReadModelsTests() =
    [<Fact>]
    member this.``A projection whose handlers take ISqlStore writes to PostgreSQL``() =
        Assert.Equal(ProjectionStoreKinds.Sql, ProjectionStoreKinds.Of typeof<SqlOrganizationProjection>)

    [<Fact>]
    member this.``A projection whose handlers take INoSqlStore, or no store, writes to the document store``() =
        Assert.Equal(ProjectionStoreKinds.Documents, ProjectionStoreKinds.Of typeof<OrganizationProjection>)
        Assert.Equal(ProjectionStoreKinds.Documents, ProjectionStoreKinds.Of typeof<NoStoreOrganizationProjection>)

    [<Fact>]
    member this.``A projection can't write to both stores``() =
        let ex = Assert.Throws<InvalidOperationException>(fun () -> ProjectionStoreKinds.Of typeof<MixedOrganizationProjection> |> ignore)
        Assert.Contains("Split it into two projections", ex.Message)

    [<Fact>]
    member this.``A read model's key is its [<Key>] properties in order, or its Id``() =
        let hour = { AreaId = "Areas-1"; HourStart = DateTime(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc); Guests = 4 }

        Assert.Equal<obj list>([ box "Areas-1"; box hour.HourStart ], List.ofArray (ProjectionKeys.KeyOf hour))
        Assert.Equal<obj list>([ box "Organizations-1" ], List.ofArray (ProjectionKeys.KeyOf { Organization.Id = "Organizations-1"; Name = ""; DateRegistered = DateTime.MinValue }))
        Assert.Equal<obj list>([ box "Areas-1"; box hour.HourStart ], List.ofArray (ProjectionKeys.ValuesOf (box ("Areas-1", hour.HourStart))))

    [<Fact>]
    member this.``The TestKit's store is the SQL store too, with composite keys``() =
        task {
            let store = TestKitProjectionStore() :> ISqlStore
            let hour = { AreaId = "Areas-1"; HourStart = DateTime(2026, 11, 11, 18, 0, 0, DateTimeKind.Utc); Guests = 4 }

            do! store.StoreAsync(hour)
            let! loaded = store.LoadAsync<AreaHour>(box ("Areas-1", hour.HourStart))
            let! many = store.LoadManyAsync<AreaHour>([| box ("Areas-1", hour.HourStart); box ("Areas-2", hour.HourStart) |])
            do! store.DeleteAsync<AreaHour>(box ("Areas-1", hour.HourStart))
            let! deleted = store.LoadAsync<AreaHour>(box ("Areas-1", hour.HourStart))

            Assert.Equal(hour, loaded)
            Assert.Equal(hour, many.[box ("Areas-1", hour.HourStart)])
            Assert.True(isNull (box many.[box ("Areas-2", hour.HourStart)]))
            Assert.True(isNull (box deleted))
        }

    [<Fact>]
    member this.``Read models of different types with the same key are different rows``() =
        task {
            let store = TestKitProjectionStore() :> IProjectionsStore
            do! store.StoreAsync({ Organization.Id = "1"; Name = "Rosetta"; DateRegistered = DateTime.MinValue })
            do! store.StoreAsync({ Tally.Id = "1"; Count = 3 })

            let! organization = store.LoadAsync<Organization>("1")
            let! tally = store.LoadAsync<Tally>("1")

            Assert.Equal("Rosetta", organization.Name)
            Assert.Equal(3, tally.Count)
        }
