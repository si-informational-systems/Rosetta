namespace SI.Rosetta.IntegrationTests

open System
open Xunit
open SI.Rosetta.Persistence.RavenDB
open SI.Rosetta.Projections
open SI.Rosetta.Projections.RavenDB

type RavenDbProjectionsStoreTests(databases: Databases) =
    let DocumentStore =
        RavenDocumentStoreFactory.CreateAndInitializeDocumentStore
            (RavenConfig.FromConfiguration(databases.Configuration "Rosetta-RavenDbProjectionsStoreTests")) None
    let Store = RavenDbProjectionsStore(DocumentStore) :> IProjectionsStore

    let NewParcel () =
        let id = sprintf "Parcels-%s" (Guid.NewGuid().ToString("N"))
        { Id = id; Recipient = "Ada" } : Parcel

    interface IDisposable with
        member _.Dispose() = DocumentStore.Dispose()

    [<Fact>]
    member this.``DeleteAsync deletes the document``() =
        task {
            let parcel = NewParcel()
            do! Store.StoreAsync(parcel)

            do! Store.DeleteAsync<Parcel>(parcel.Id)

            let! deleted = Store.LoadAsync<Parcel>(parcel.Id)
            Assert.True(isNull (box deleted))
        }

    [<Fact>]
    member this.``DeleteInUnitOfWorkAsync deletes every document``() =
        task {
            let parcels = [| NewParcel(); NewParcel() |]
            do! Store.StoreInUnitOfWorkAsync(parcels)

            do! Store.DeleteInUnitOfWorkAsync<Parcel>(parcels |> Array.map (fun parcel -> box parcel.Id))

            let! remaining = Store.LoadManyAsync<Parcel>(parcels |> Array.map (fun parcel -> box parcel.Id))
            Assert.All(remaining.Values, fun parcel -> Assert.True(isNull (box parcel)))
        }
