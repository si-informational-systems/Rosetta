namespace SI.Rosetta.Projections.UnitTests

open MongoDB.Bson
open MongoDB.Bson.Serialization
open Xunit
open SI.Rosetta.Persistence.MongoDB

type Dimensions =
    | Small
    | Crate of Width: int * Height: int
    | Labelled of string * Stop

and [<CLIMutable>] Stop = { Name: string; Position: int }

type Signal =
    | Red
    | Green

[<CLIMutable>]
type Route = { Id: string; Stops: Stop list; Recipients: string list }

[<CLIMutable>]
type Totals = { Id: string; ByName: Map<string, int>; ByDay: Map<int, string list> }

[<CLIMutable>]
type Package = { Id: string; Size: Dimensions; Signal: Signal; Note: string option; Parcels: Dimensions list }

/// Rosetta's BSON serializers for F# types, without a server: written as BSON, read back
type MongoFSharpSerializationTests() =
    do MongoConventions.Register()

    let RoundTrip (value: 'T) =
        let document = value.ToBsonDocument()
        document, BsonSerializer.Deserialize<'T>(document)

    [<Fact>]
    member this.``Lists are arrays, and are read back, records in them camelCased``() =
        let route = { Id = "Routes-1"; Stops = [ { Name = "Depot"; Position = 1 } ]; Recipients = [ "Ada"; "Grace" ] }

        let document, read = RoundTrip route

        Assert.Equal("""{ "_id" : "Routes-1", "stops" : [{ "name" : "Depot", "position" : 1 }], "recipients" : ["Ada", "Grace"] }""", document.ToJson())
        Assert.Equal(route, read)

    [<Fact>]
    member this.``An empty or null list reads as an empty list``() =
        let read = BsonSerializer.Deserialize<Route>(BsonDocument.Parse("""{ "_id" : "Routes-1", "stops" : [], "recipients" : null }"""))

        Assert.Equal<Stop list>([], read.Stops)
        Assert.Equal<string list>([], read.Recipients)

    [<Fact>]
    member this.``A string-keyed map is a document, any other an array of key-value pairs``() =
        let totals = { Id = "Totals-1"; ByName = Map [ "Ada", 1; "Grace", 2 ]; ByDay = Map [ 1, [ "a" ]; 2, [] ] }

        let document, read = RoundTrip totals

        Assert.Equal("""{ "_id" : "Totals-1", "byName" : { "Ada" : 1, "Grace" : 2 }, "byDay" : [[1, ["a"]], [2, []]] }""", document.ToJson())
        Assert.Equal(totals, read)

    [<Fact>]
    member this.``A union is its case and its fields``() =
        let package =
            { Id = "Packages-1"; Size = Labelled("fragile", { Name = "Depot"; Position = 1 }); Signal = Green; Note = None
              Parcels = [ Small; Crate(2, 3) ] }

        let document, read = RoundTrip package

        Assert.Equal(
            """{ "_id" : "Packages-1", "size" : { "case" : "Labelled", "fields" : ["fragile", { "name" : "Depot", "position" : 1 }] }, """ +
            """"signal" : { "case" : "Green" }, "note" : null, "parcels" : [{ "case" : "Small" }, { "case" : "Crate", "fields" : [2, 3] }] }""",
            document.ToJson())
        Assert.Equal(package, read)

    [<Fact>]
    member this.``Options are written as before``() =
        let document, read = RoundTrip { Id = "Packages-1"; Size = Small; Signal = Red; Note = Some "x"; Parcels = [] }

        Assert.Equal("""{ "value" : "x" }""", document.["note"].ToJson())
        Assert.Equal(Some "x", read.Note)

    [<Fact>]
    member this.``Unions as the driver wrote them before are read as they were``() =
        // A fieldless case by its class name, a case whose fields weren't written, and a union of fieldless cases as {}
        let read =
            BsonSerializer.Deserialize<Package>(BsonDocument.Parse(
                """{ "_id" : "Packages-1", "size" : { "_t" : "_Small" }, "signal" : { }, "note" : null, "parcels" : [{ "_t" : "Crate" }, { "_t" : "Small" }] }"""))

        Assert.Equal(Small, read.Size)
        Assert.Equal(Red, read.Signal)
        Assert.Equal<Dimensions list>([ Crate(0, 0); Small ], read.Parcels)

    [<Fact>]
    member this.``An unknown case fails``() =
        let ex = Assert.ThrowsAny<exn>(fun () ->
                    BsonSerializer.Deserialize<Package>(BsonDocument.Parse("""{ "_id" : "Packages-1", "size" : { "case" : "Huge" } }""")) |> ignore)

        Assert.Contains("Huge", ex.GetBaseException().Message)
