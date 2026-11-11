namespace SI.Rosetta.IntegrationTests

open System
open System.Threading.Tasks
open Xunit
open FSharp.Control
open KurrentDB.Client
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.KurrentDB
open SI.Rosetta.Common

type KurrentDBAggregateRepositoryTests(databases: Databases) =
    let Client = databases.KurrentDBClient

    // Not the Parcels category, which HostingTests projects
    let NewId () = sprintf "StoredParcels-%s" (Guid.NewGuid().ToString("N"))

    let Get (repository: IAggregateRepository) (id: string) =
        task {
            let! state = repository.GetAsync<ParcelAggregateState>(id)
            return state |> Option.map (fun state -> ParcelAggregate(State = state))
        }

    [<Fact>]
    member this.``An aggregate is read back as it was stored``() =
        task {
            let repository = KurrentDBAggregateRepository(Client) :> IAggregateRepository
            let id = NewId()
            let parcel = ParcelAggregate()
            parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            parcel.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            do! repository.StoreAsync(parcel)

            let! stored = Get repository id

            Assert.Equal("Grace", stored.Value.State.Recipient)
            Assert.Equal(2L, stored.Value.Version)
        }

    [<Fact>]
    member this.``A stored event's type is its stored name, and its metadata only KurrentDB.Client's trace``() =
        task {
            let repository = KurrentDBAggregateRepository(Client) :> IAggregateRepository
            let id = NewId()
            let parcel = ParcelAggregate()
            parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            do! repository.StoreAsync(parcel)
            let! stored = Get repository id
            let traced = stored.Value
            traced.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            let command = (new Diagnostics.Activity("ReaddressParcel")).SetIdFormat(Diagnostics.ActivityIdFormat.W3C).Start()
            do! repository.StoreAsync(traced)
            command.Stop()

            let! events = Client.ReadStreamAsync(Direction.Forwards, id, StreamPosition.Start) |> TaskSeq.toListAsync

            Assert.Equal<string list>([ "ParcelRegistered"; "ParcelReaddressed" ], events |> List.map (fun e -> e.Event.EventType))
            // Outside a trace: no metadata at all
            Assert.True(events.[0].Event.Metadata.IsEmpty)
            use metadata = Text.Json.JsonDocument.Parse(events.[1].Event.Metadata)
            // Rosetta writes none: KurrentDB.Client adds $traceId and $spanId to an append inside a trace
            Assert.Equal<string list>([ "$traceId"; "$spanId" ], metadata.RootElement.EnumerateObject() |> Seq.map (fun p -> p.Name) |> List.ofSeq)
            Assert.Equal(command.TraceId.ToHexString(), metadata.RootElement.GetProperty("$traceId").GetString())
        }

    [<Fact>]
    member this.``Storing over a concurrent change throws ConcurrencyException``() =
        task {
            let repository = KurrentDBAggregateRepository(Client) :> IAggregateRepository
            let id = NewId()
            let parcel = ParcelAggregate()
            parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            do! repository.StoreAsync(parcel)

            let! first = Get repository id
            let! second = Get repository id
            first.Value.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            second.Value.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Linus" })
            do! repository.StoreAsync(first.Value)

            let! _ = Assert.ThrowsAsync<ConcurrencyException>(fun () -> repository.StoreAsync(second.Value))
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

    [<Fact>]
    member this.``Two commands racing on one aggregate both go through: the one that lost is retried at once``() =
        task {
            let repository = KurrentDBAggregateRepository(Client) :> IEventSourcedAggregateRepository
            let id = NewId()
            let handler () = ParcelAggregateHandler(repository) :> IAggregateHandler
            do! handler().ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })

            let! _ =
                Task.WhenAll(
                    handler().ExecuteAsync(ParcelCommands.Readdress { Id = id; Recipient = "Grace" }),
                    handler().ExecuteAsync(ParcelCommands.Readdress { Id = id; Recipient = "Linus" }))

            let! stored = Get repository id
            Assert.Equal(3L, stored.Value.Version)
            Assert.Contains(stored.Value.State.Recipient, [ "Grace"; "Linus" ])
        }
