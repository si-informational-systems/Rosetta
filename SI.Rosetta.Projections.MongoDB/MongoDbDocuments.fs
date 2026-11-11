namespace SI.Rosetta.Projections.MongoDB

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open SI.Rosetta.Common

/// The writes behind the MongoDB store, in a session's transaction or on their own (a null session)
[<AbstractClass; Sealed>]
type internal MongoDbDocuments =
    /// The 'T documents' collection: the pluralized type name
    static member Collection<'T>(db: IMongoDatabase) =
        db.GetCollection<'T>(PluralizeName(typeof<'T>.Name))

    /// Upserts the document by its Id: Id on insert, every other property set
    static member UpsertAsync<'T>(db: IMongoDatabase, session: IClientSessionHandle, doc: 'T, cancellationToken: CancellationToken) : Task =
        task {
            let docType = typeof<'T>
            let idProp = docType.GetProperty("Id")
            let id = if idProp <> null then idProp.GetValue(doc) else null
            let filter = Builders<'T>.Filter.Eq("Id", id.ToString())

            // Build update definition: SetOnInsert for Id, Set for other properties
            let mutable updateDef = Builders<'T>.Update.SetOnInsert("Id", id)
            for prop in docType.GetProperties() do
                if prop.Name <> "Id" then
                    let value = prop.GetValue(doc)
                    updateDef <- updateDef.Set(prop.Name, value)

            let options = UpdateOptions(IsUpsert = true)
            let collection = MongoDbDocuments.Collection<'T> db
            // In the session, or the write isn't part of its transaction
            let! _ =
                if isNull session then collection.UpdateOneAsync(filter, updateDef, options, cancellationToken)
                else collection.UpdateOneAsync(session, filter, updateDef, options, cancellationToken)
            ()
        }

    static member DeleteAsync<'T>(db: IMongoDatabase, session: IClientSessionHandle, id: string, cancellationToken: CancellationToken) : Task =
        task {
            let filter = Builders<'T>.Filter.Eq("Id", id)
            let collection = MongoDbDocuments.Collection<'T> db
            let! _ =
                if isNull session then collection.DeleteOneAsync(filter, cancellationToken)
                else collection.DeleteOneAsync(session, filter, cancellationToken = cancellationToken)
            ()
        }
