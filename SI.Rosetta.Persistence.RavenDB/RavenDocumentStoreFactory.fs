namespace SI.Rosetta.Persistence.RavenDB

open System
open System.Security.Cryptography.X509Certificates
open Microsoft.Extensions.Configuration
open Raven.Client.Documents
open Raven.Client.Documents.Conventions
open Raven.Client.Documents.Operations
open Raven.Client.Exceptions
open Raven.Client.Exceptions.Database
open Raven.Client.ServerWide
open Raven.Client.ServerWide.Operations

type RavenConfig =
    { Urls: string[]
      DatabaseName: string
      CertificateFilePath: string option
      CertificateFilePassword: string option }
    
    static member FromConfiguration(conf: IConfiguration) =
        { Urls = conf.["RavenDB:Urls"].Split(';')
          DatabaseName = conf.["RavenDB:DatabaseName"]
          CertificateFilePassword = Option.ofObj conf.["RavenDB:CertificatePassword"]
          CertificateFilePath = Option.ofObj conf.["RavenDB:CertificatePath"] }

module RavenDocumentStoreFactory =

    let private RunDummyQueryToAvoidLazyLoading (store: IDocumentStore) =
        using (store.OpenSession()) (fun ses ->
            ses.Load<obj>("id") |> ignore
        )

    let private EnsureDatabaseExists (store: IDocumentStore) (databaseName: string) =
        let db = if String.IsNullOrWhiteSpace databaseName then store.Database else databaseName
        
        if String.IsNullOrWhiteSpace db then
            invalidArg "databaseName" "Value cannot be null or whitespace."

        try
            store.Maintenance.ForDatabase(db).Send(new GetStatisticsOperation()) |> ignore
        with
        | :? DatabaseDoesNotExistException ->
            try
                store.Maintenance.Server.Send(new CreateDatabaseOperation(new DatabaseRecord(db))) |> ignore
            with
            | :? ConcurrencyException -> () // Already created

    let private InternalCreate (conf: RavenConfig) (setupConventions: (DocumentConventions -> unit) option) =
        let store = new DocumentStore(Urls = conf.Urls, Database = conf.DatabaseName)
        
        // Handle Certificate
        match conf.CertificateFilePath with
        | Some path when not (String.IsNullOrWhiteSpace path) ->
            let password = defaultArg conf.CertificateFilePassword null
            store.Certificate <- X509CertificateLoader.LoadPkcs12FromFile(path, password)
        | _ -> ()

        // Handle Conventions
        setupConventions |> Option.iter (fun action -> action store.Conventions)
        
        store.Initialize()

    /// Creation with auto-database setup
    let CreateAndInitializeDocumentStore (conf: RavenConfig) (setupConventions: (DocumentConventions -> unit) option) =
        let store = InternalCreate conf setupConventions
        EnsureDatabaseExists store conf.DatabaseName
        RunDummyQueryToAvoidLazyLoading store
        store