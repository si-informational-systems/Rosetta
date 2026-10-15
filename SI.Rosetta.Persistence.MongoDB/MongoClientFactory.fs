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
      AuthSource: string
      /// A full mongodb:// or mongodb+srv:// connection string, for TLS, replica sets or any other option.
      /// When set, it's used instead of Host, Port, Username, Password and AuthSource
      ConnectionString: string }

    // MongoUrlBuilder escapes the credentials, so a password may hold @ : / ? # or %
    member private x.CreateUrlBuilder() =
        if not (String.IsNullOrWhiteSpace x.ConnectionString) then
            MongoUrlBuilder(x.ConnectionString)
        else
            let builder = MongoUrlBuilder()
            let host = x.Host.TrimEnd('/')
            builder.Server <-
                if String.IsNullOrEmpty x.Port then MongoServerAddress.Parse host
                else MongoServerAddress(host, int x.Port)
            if not (String.IsNullOrEmpty x.Username || String.IsNullOrEmpty x.Password) then
                builder.Username <- x.Username
                builder.Password <- x.Password
                if not (String.IsNullOrEmpty x.AuthSource) then
                    builder.AuthenticationSource <- x.AuthSource
            builder

    member x.ToMongoUrl() : MongoUrl =
        x.CreateUrlBuilder().ToMongoUrl()

    member x.GenerateConnectionString() =
        x.CreateUrlBuilder().ToString()

    member x.GenerateConnectionStringWithDatabase() =
        let builder = x.CreateUrlBuilder()
        builder.DatabaseName <- x.DatabaseName
        builder.ToString()

    static member FromConfiguration(conf: IConfiguration) =
        let connectionString = conf.["MongoDB:ConnectionString"]
        { Host = conf.["MongoDB:Host"]
          Port = conf.["MongoDB:Port"]
          DatabaseName =
            match conf.["MongoDB:DatabaseName"] with
            // Taken from the connection string's path when it's not set on its own
            | name when String.IsNullOrWhiteSpace name && not (String.IsNullOrWhiteSpace connectionString) ->
                MongoUrl(connectionString).DatabaseName
            | name -> name
          Username = conf.["MongoDB:Username"]
          Password = conf.["MongoDB:Password"]
          AuthSource = conf.["MongoDB:AuthSource"]
          ConnectionString = connectionString }

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
        new MongoClient(conf.ToMongoUrl())

    let CreateClientWithDatabase (conf: MongoConfig) =
        new MongoClient(conf.GenerateConnectionStringWithDatabase())
