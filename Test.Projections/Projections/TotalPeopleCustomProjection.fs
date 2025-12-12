namespace TestFSharp

open SI.Rosetta.Projections
open SI.Rosetta.Common

[<CLIMutable>]
type TotalPeople = {
    mutable Id: string
    TotalPersons: int
    TotalOrganizations: int
}

[<CustomProjectionStream(ProjectionStreamNames.TotalPeopleAndOrganizationsCustomStream)>]
type TotalPeopleAndOrganizationsCustomProjectionEvents = 
    | Registered of PersonRegistered
    | NameChanged of PersonNameChanged
    | OrganizationRegistered of OrganizationRegistered
    interface ICustomProjectionEvents

[<HandlesStream(ProjectionStreamNames.TotalPeopleAndOrganizationsCustomStream)>]
type TotalPeopleCustomProjection() =
    inherit Projection<TotalPeopleAndOrganizationsCustomProjectionEvents>()
    interface IAmHandledBy<TotalPeopleCustomProjectionHandler, TotalPeopleAndOrganizationsCustomProjectionEvents>
    interface IAmHandledBy<TotalJohnsCustomProjectionHandler, TotalPeopleAndOrganizationsCustomProjectionEvents>

and TotalPeopleCustomProjectionHandler(store: INoSqlStore) =
    let Store = store
    
    interface IProjectionHandler<TotalPeopleAndOrganizationsCustomProjectionEvents> with
        member this.Handle(event: TotalPeopleAndOrganizationsCustomProjectionEvents, checkpoint: uint64) =
            task {
                match event with
                | TotalPeopleAndOrganizationsCustomProjectionEvents.Registered _ ->
                    do! this.Project(fun custom ->
                        { custom with
                            TotalPersons = custom.TotalPersons + 1 }).ConfigureAwait(false)

                | TotalPeopleAndOrganizationsCustomProjectionEvents.NameChanged _ -> ()

                | TotalPeopleAndOrganizationsCustomProjectionEvents.OrganizationRegistered _ ->
                    do! this.Project(fun custom ->
                        { custom with
                            TotalOrganizations = custom.TotalOrganizations + 1 }).ConfigureAwait(false)
            }
            
    member private this.Project(update: TotalPeople -> TotalPeople) =
        task {
            let id = "TotalPeople-All"
            let! doc = Store.LoadAsync<TotalPeople>(id).ConfigureAwait(false)
            let custom = 
                if isNull (box doc) then 
                    { Id = id
                      TotalPersons = 0
                      TotalOrganizations = 0 }
                else doc
            let updated = update custom
            do! Store.StoreAsync(updated).ConfigureAwait(false)
        }

and TotalJohnsCustomProjectionHandler(store: INoSqlStore) =
    let Store = store
    
    interface IProjectionHandler<TotalPeopleAndOrganizationsCustomProjectionEvents> with
        member this.Handle(event: TotalPeopleAndOrganizationsCustomProjectionEvents, checkpoint: uint64) =
            task {
                match event with
                | TotalPeopleAndOrganizationsCustomProjectionEvents.Registered ev ->
                    if (ev.Name = "John") then
                        do! this.Project(fun totalPeople ->
                            { totalPeople with
                                TotalPersons = totalPeople.TotalPersons + 1 }).ConfigureAwait(false)

                | TotalPeopleAndOrganizationsCustomProjectionEvents.NameChanged ev ->
                   do! this.Project(fun totalPeople -> 
                       if (ev.Name = "John") then
                           { totalPeople with TotalPersons = totalPeople.TotalPersons + 1 }
                       else
                           { totalPeople with TotalPersons = totalPeople.TotalPersons - 1 }
                       ).ConfigureAwait(false)

                | TotalPeopleAndOrganizationsCustomProjectionEvents.OrganizationRegistered _ ->
                    do! this.Project(fun totalPeople ->
                        { totalPeople with
                            TotalOrganizations = totalPeople.TotalOrganizations + 1 }).ConfigureAwait(false)
            }
            
    member private this.Project(update: TotalPeople -> TotalPeople) =
        task {
            let id = "TotalPeople-Johns"
            let! doc = Store.LoadAsync<TotalPeople>(id).ConfigureAwait(false)
            let custom = 
                if isNull (box doc) then 
                    { Id = id
                      TotalPersons = 0
                      TotalOrganizations = 0 }
                else doc
            let updated = update custom
            do! Store.StoreAsync(updated).ConfigureAwait(false)
        }

