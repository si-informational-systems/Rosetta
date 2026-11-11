namespace SI.Rosetta.Persistence.PostgreSQL

open System
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Npgsql

type PostgreSqlConfig =
    { ConnectionString: string
      /// The schema of the read model tables that don't name theirs ([<Table(…, Schema = …)>]): public unless set
      Schema: string }

    /// From PostgreSQL:ConnectionString, which is required, and PostgreSQL:Schema
    static member FromConfiguration(conf: IConfiguration) =
        match conf.["PostgreSQL:ConnectionString"] with
        | connectionString when String.IsNullOrWhiteSpace connectionString ->
            raise (InvalidOperationException(
                "PostgreSQL:ConnectionString is missing from the configuration " +
                "(for example Host=localhost;Port=5432;Database=Rosetta;Username=postgres;Password=…)."))
        | connectionString ->
            { ConnectionString = connectionString
              Schema =
                match conf.["PostgreSQL:Schema"] with
                | schema when String.IsNullOrWhiteSpace schema -> "public"
                | schema -> schema }

/// The PostgreSQL database Rosetta's stores use: its own data source (connection pool), apart from any the app has
type PostgreSqlDatabase(config: PostgreSqlConfig) =
    let Source = NpgsqlDataSource.Create(config.ConnectionString)

    member this.DataSource = Source

    /// The schema of the read model tables that don't name theirs
    member this.Schema = if String.IsNullOrWhiteSpace config.Schema then "public" else config.Schema

    /// The database's name, from the connection string
    member this.Name = NpgsqlConnectionStringBuilder(config.ConnectionString).Database

    /// Creates the database when it doesn't exist, as Rosetta does with RavenDB. The user needs CREATEDB for that
    member this.EnsureCreatedAsync() =
        task {
            try
                use! connection = Source.OpenConnectionAsync().ConfigureAwait(false)
                ()
            with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.InvalidCatalogName ->
                // From the server's maintenance database
                let maintenance = NpgsqlConnectionStringBuilder(config.ConnectionString, Database = "postgres", Pooling = false)
                use connection = new NpgsqlConnection(maintenance.ConnectionString)
                do! connection.OpenAsync().ConfigureAwait(false)
                use create = new NpgsqlCommand(sprintf "CREATE DATABASE \"%s\"" (this.Name.Replace("\"", "\"\"")), connection)
                try
                    let! _ = create.ExecuteNonQueryAsync().ConfigureAwait(false)
                    ()
                with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.DuplicateDatabase -> ()
        } :> Task

    interface IDisposable with
        member this.Dispose() = Source.Dispose()

    interface IAsyncDisposable with
        member this.DisposeAsync() = Source.DisposeAsync()
