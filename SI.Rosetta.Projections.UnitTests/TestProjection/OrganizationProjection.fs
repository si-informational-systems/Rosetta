namespace SI.Rosetta.Projections.UnitTests

open SI.Rosetta.Projections

type Organization = {
    mutable Id: string
    Name: string
    DateRegistered: System.DateTime
}

[<HandlesStream(ProjectionStreamNames.OrganizationStream)>]
type OrganizationProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<OrganizationProjectionHandler, OrganizationEvents>

and OrganizationProjectionHandler(store: INoSqlStore) =
    let Store = store
    
    interface IProjectionHandler<OrganizationEvents> with
        member this.Handle(event: OrganizationEvents, checkpoint: uint64) =
            task {
                match event with
                | OrganizationEvents.Registered e ->
                    let organization: Organization = {
                        Id = e.Id
                        Name = e.Name
                        DateRegistered = e.DateRegistered
                    }
                    do! Store.StoreAsync(organization).ConfigureAwait(false)

                | OrganizationEvents.NameChanged e ->
                    do! this.Project(e.Id, fun organization ->
                        { organization with Name = e.Name }).ConfigureAwait(false)
            }
            
    member private this.Project(id: string, update: Organization -> Organization) =
        task {
            let! organization = Store.LoadAsync<Organization>(id).ConfigureAwait(false)
            let updated = update organization
            do! Store.StoreAsync(updated).ConfigureAwait(false)
        }

