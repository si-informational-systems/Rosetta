namespace SI.Rosetta.Projections.UnitTests

open System
open Xunit
open SI.Rosetta.Projections
open SI.Rosetta.Projections.TestKit

[<CLIMutable>]
type GuidKeyed = { Id: Guid; Name: string }

[<CLIMutable>]
type IntKeyed = { Id: int; Name: string }

[<CLIMutable>]
type Int64Keyed = { Id: int64; Name: string }

type TestKitProjectionStoreTests() =
    let Store = TestKitProjectionStore() :> IProjectionsStore

    member private this.StoresLoadsAndDeletes<'T when 'T : not struct> (doc: 'T) (id: obj) =
        task {
            do! Store.StoreAsync(doc)
            let! loaded = Store.LoadAsync<'T>(id)
            Assert.Equal(doc, loaded)

            let! many = Store.LoadManyAsync<'T>([| id |])
            Assert.Equal(doc, many.[id])

            do! Store.DeleteAsync<'T>(id)
            let! deleted = Store.LoadAsync<'T>(id)
            Assert.True(isNull (box deleted))
        }

    [<Fact>]
    member this.``Guid Ids are stored, loaded and deleted``() =
        let id = Guid.NewGuid()
        this.StoresLoadsAndDeletes { GuidKeyed.Id = id; Name = "Guid" } id

    [<Fact>]
    member this.``int Ids are stored, loaded and deleted``() =
        this.StoresLoadsAndDeletes { IntKeyed.Id = 7; Name = "int" } 7

    [<Fact>]
    member this.``int64 Ids are stored, loaded and deleted``() =
        this.StoresLoadsAndDeletes { Int64Keyed.Id = 7L; Name = "int64" } 7L

    [<Fact>]
    member this.``A missing document loads as null``() =
        task {
            let! missing = Store.LoadAsync<Organization>("Organizations-404")
            Assert.True(isNull (box missing))
        }
