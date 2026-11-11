namespace SI.Rosetta.IntegrationTests

open System
open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Driver
open Xunit
open SI.Rosetta.Persistence.MongoDB
open SI.Rosetta.Projections
open SI.Rosetta.Projections.MongoDB

/// Stored in Couriers, whose names are unique
[<CLIMutable>]
type Courier = { Id: string; Name: string }

/// Read from Faults, a view that fails every query
[<CLIMutable>]
type Fault = { Id: string; Ratio: int }

/// F# lists, maps and unions, which Rosetta's serializers write and read back
type Leg =
    | Walk
    | Drive of Vehicle: string * Stops: string list

[<CLIMutable>]
type Itinerary = { Id: string; Legs: Leg list; Recipients: string list; Weights: Map<string, decimal>; ByStop: Map<int, Leg> }

type MongoDbProjectionsStoreTests(databases: Databases) =
    do MongoConventions.Register()
    let Client = new MongoClient(databases.MongoReplicaSetUrl)
    let Database = Client.GetDatabase("Rosetta-MongoDbProjectionsStoreTests")
    let Store = MongoDbProjectionsStore(Client, Database) :> IProjectionsStore

    let NewId (prefix: string) = sprintf "%s-%s" prefix (Guid.NewGuid().ToString("N"))

    interface IDisposable with
        member _.Dispose() = Client.Dispose()

    [<Fact>]
    member this.``StoreManyAsync stores every document``() =
        task {
            let couriers = [| { Id = NewId "Couriers"; Name = NewId "Ada" }; { Id = NewId "Couriers"; Name = NewId "Grace" } |]

            do! Store.StoreManyAsync(couriers)

            let! stored = Store.LoadManyAsync<Courier>(couriers |> Array.map (fun courier -> box courier.Id))
            Assert.Equal<Courier>(couriers, couriers |> Array.map (fun courier -> stored.[courier.Id]))
        }

    [<Fact>]
    member this.``StoreManyAsync stores nothing when one write fails``() =
        task {
            let couriers = Database.GetCollection<BsonDocument>("Couriers")
            let! _ = couriers.Indexes.CreateOneAsync(CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("name"), CreateIndexOptions(Unique = true)))
            let name = NewId "Same"
            // The second one breaks the unique index on name
            let sameName = [| { Id = NewId "Couriers"; Name = name }; { Id = NewId "Couriers"; Name = name } |]

            let! _ = Assert.ThrowsAnyAsync<MongoException>(fun () -> Store.StoreManyAsync(sameName))

            let! stored = couriers.CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("name", name))
            Assert.Equal(0L, stored)
        }

    [<Fact>]
    member this.``LoadAsync and LoadManyAsync throw server errors instead of returning nothing``() =
        task {
            // A view whose every query fails on the server
            let! _ = Database.GetCollection<BsonDocument>("FaultSources").InsertOneAsync(BsonDocument("_id", BsonString "Faults-1"))
            let failingPipeline = [| BsonDocument("$project", BsonDocument("ratio", BsonDocument("$divide", BsonArray([ BsonInt32 1; BsonInt32 0 ])))) |]
            do! Database.CreateViewAsync<BsonDocument, BsonDocument>("Faults", "FaultSources", PipelineDefinition<BsonDocument, BsonDocument>.Create(failingPipeline))

            let! _ = Assert.ThrowsAnyAsync<MongoException>(fun () -> Store.LoadAsync<Fault>("Faults-1") :> Task)
            let! _ = Assert.ThrowsAnyAsync<MongoException>(fun () -> Store.LoadManyAsync<Fault>([| "Faults-1" |]) :> Task)
            ()
        }

    [<Fact>]
    member this.``F# lists, maps and unions are stored and loaded``() =
        task {
            let itinerary =
                { Id = NewId "Itineraries"
                  Legs = [ Walk; Drive("van", [ "Depot"; "Ada" ]) ]
                  Recipients = [ "Ada"; "Grace" ]
                  Weights = Map [ "Parcels-1", 1.5m ]
                  ByStop = Map [ 1, Walk; 2, Drive("bike", []) ] }

            do! Store.StoreAsync(itinerary)
            let! loaded = Store.LoadAsync<Itinerary>(itinerary.Id)
            let! many = Store.LoadManyAsync<Itinerary>([| box itinerary.Id |])

            Assert.Equal(itinerary, loaded)
            Assert.Equal(itinerary, many.[itinerary.Id])
        }
