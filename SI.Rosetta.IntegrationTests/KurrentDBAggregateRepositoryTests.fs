namespace SI.Rosetta.IntegrationTests

open System
open System.Threading.Tasks
open Xunit
open KurrentDB.Client
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.KurrentDB
open SI.Rosetta.Common

type KurrentDBAggregateRepositoryTests(databases: Databases) =
    let Client = databases.KurrentDBClient

    // Not the Parcels category, which HostingTests projects
    let NewId () = sprintf "StoredParcels-%s" (Guid.NewGuid().ToString("N"))

    let Get (repository: IAggregateRepository) (id: string) =
        repository.GetAsync<ParcelAggregate, ParcelAggregateState, ParcelEvents>(id)

    [<Fact>]
    member this.``An aggregate is read back as it was stored``() =
        task {
            let repository = KurrentDBAggregateRepository(Client) :> IAggregateRepository
            let id = NewId()
            let parcel = ParcelAggregate()
            parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            parcel.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            do! repository.StoreAsync<ParcelAggregate, ParcelAggregateState>(parcel)

            let! stored = Get repository id

            Assert.Equal("Grace", stored.Value.State.Recipient)
            Assert.Equal(2L, stored.Value.Version)
        }

    [<Fact>]
    member this.``Storing over a concurrent change throws ConcurrencyException``() =
        task {
            let repository = KurrentDBAggregateRepository(Client) :> IAggregateRepository
            let id = NewId()
            let parcel = ParcelAggregate()
            parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            do! repository.StoreAsync<ParcelAggregate, ParcelAggregateState>(parcel)

            let! first = Get repository id
            let! second = Get repository id
            first.Value.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            second.Value.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Linus" })
            do! repository.StoreAsync<ParcelAggregate, ParcelAggregateState>(first.Value)

            let! _ = Assert.ThrowsAsync<ConcurrencyException>(fun () -> repository.StoreAsync<ParcelAggregate, ParcelAggregateState>(second.Value))
            ()
        }

    [<Fact>]
    member this.``An event the events DU has no case for fails the read``() =
        task {
            let repository = KurrentDBAggregateRepository(Client) :> IAggregateRepository
            let id = NewId()
            let! _ = Client.AppendToStreamAsync(id, StreamState.NoStream, [
                        Events.Known (ParcelEvents.Registered { Id = id; Recipient = "Ada" })
                        Events.Written "ParcelLost" (sprintf """{"Id":"%s"}""" id) ])

            let! ex = Assert.ThrowsAsync<UnknownEventException>(fun () -> Get repository id :> Task)

            Assert.Equal("ParcelLost", ex.TypeName)
            Assert.Equal(id, ex.StreamName)
            Assert.Equal(1UL, ex.EventNumber)
        }

    [<Fact>]
    member this.``Upcasters turn old events into current ones while reading``() =
        task {
            let repository = KurrentDBAggregateRepository(Client, [ ParcelCreatedUpcaster() :> IEventUpcaster ]) :> IAggregateRepository
            let id = NewId()
            let! _ = Client.AppendToStreamAsync(id, StreamState.NoStream, [
                        Events.Written "ParcelCreated" (sprintf """{"Id":"%s","Addressee":"Ada"}""" id) ])

            let! stored = Get repository id

            Assert.Equal(id, stored.Value.State.Id)
            Assert.Equal("Ada", stored.Value.State.Recipient)
        }
