namespace SI.Rosetta.Persistence.MongoDB

open System
open Microsoft.Extensions.Configuration
open MongoDB.Bson
open MongoDB.Bson.Serialization.Conventions
open MongoDB.Driver

type MongoConfig =
    { Host: string
      Port: string
      DatabaseName: string
      Username: string
      Password: string
      AuthSource: string }

    member x.GenerateConnectionString() =
        let cleanHost = x.Host.TrimEnd('/')
        let portPart = if not (String.IsNullOrEmpty x.Port) then $":{x.Port}" else ""
        let hostAndPort = $"{cleanHost}{portPart}"

        if String.IsNullOrEmpty x.Username || String.IsNullOrEmpty x.Password then
            $"mongodb://{hostAndPort}"
        else
            $"mongodb://{x.Username}:{x.Password}@{hostAndPort}?authSource={x.AuthSource}"

    member x.GenerateConnectionStringWithDatabase() =
        let cleanHost = x.Host.TrimEnd('/')
        let portPart = if not (String.IsNullOrEmpty x.Port) then $":{x.Port}" else ""
        let hostAndPort = $"{cleanHost}{portPart}"

        if String.IsNullOrEmpty x.Username || String.IsNullOrEmpty x.Password then
            $"mongodb://{hostAndPort}/{x.DatabaseName}"
        else
            $"mongodb://{x.Username}:{x.Password}@{hostAndPort}/{x.DatabaseName}?authSource={x.AuthSource}"

    static member FromConfiguration(conf: IConfiguration) =
        { Host = conf.["MongoDB:Host"]
          Port = conf.["MongoDB:Port"]
          DatabaseName = conf.["MongoDB:DatabaseName"]
          Username = conf.["MongoDB:Username"]
          Password = conf.["MongoDB:Password"]
          AuthSource = conf.["MongoDB:AuthSource"] }

module MongoConventions =
    let private Lock = obj()
    let mutable private Registered = false

    /// Registers Rosetta's BSON conventions (camelCase, ignored extra elements, enums as strings) once per process,
    /// so documents look the same whether Aggregates, Projections or both use MongoDB
    let Register() =
        lock Lock (fun () ->
            if not Registered then
                let conventions = ConventionPack()
                conventions.Add(CamelCaseElementNameConvention())
                conventions.Add(IgnoreExtraElementsConvention(true))
                conventions.Add(EnumRepresentationConvention(BsonType.String))
                ConventionRegistry.Register("FSharpConventions", conventions, fun _ -> true)
                Registered <- true)

module MongoClientFactory =

    let CreateClientWithoutDatabase (conf: MongoConfig) =
        new MongoClient(conf.GenerateConnectionString())

    let CreateClientWithDatabase (conf: MongoConfig) =
        new MongoClient(conf.GenerateConnectionStringWithDatabase())
