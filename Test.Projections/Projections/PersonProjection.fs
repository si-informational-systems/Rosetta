namespace TestFSharp

open SI.Rosetta.Projections
open System.Collections.Generic

[<CLIMutable>]
type PersonA = {
    Id: string
    Name: string
    Height: int
    Status: string
    Records: List<Record>
    Metadata: MessageMetadata
}

[<HandlesStream(ProjectionStreamNames.PersonStream)>]
type PersonProjection() =
    inherit Projection<PersonEvents>()
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents>

and PersonProjectionHandler(store: INoSqlStore) =
    let Store = store
    
    interface IProjectionHandler<PersonEvents> with
        member this.Handle(event: PersonEvents, checkpoint: uint64) =
            task {
                match event with
                | PersonEvents.Registered e ->
                    let person: PersonA = {
                        Id = e.Id
                        Records = List<Record>()
                        Name = e.Name
                        Height = 0
                        Status = "Active"
                        Metadata = e.Metadata
                    }
                    do! Store.StoreAsync(person).ConfigureAwait(false)

                | PersonEvents.RecordAdded e ->
                    do! this.Project(e.Id, fun person ->
                        person.Records.Add(e.Record)
                        person
                        ).ConfigureAwait(false)
                | PersonEvents.NameChanged e ->
                    do! this.Project(e.Id, fun person ->
                        { person with Name = e.Name }).ConfigureAwait(false)
                | PersonEvents.NameChangedToJohn e -> ()
                | PersonEvents.HeightSet e ->
                    do! this.Project(e.Id, fun person ->
                        { person with Height = e.Height }).ConfigureAwait(false)
            }
            
    member private this.Project(id: string, update: PersonA -> PersonA) =
        task {
            let! person = Store.LoadAsync<PersonA>(id).ConfigureAwait(false)
            let updated = update person
            do! Store.StoreAsync(updated).ConfigureAwait(false)
        }

