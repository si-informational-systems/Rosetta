namespace TestFSharp

open SI.Rosetta.Projections
open SI.Rosetta.Common

type TotalPeople = {
    mutable Id: string
    TotalPersons: int
    TotalOrganizations: int
}

[<CustomProjectionStream(ProjectionStreamNames.TotalPeopleStream)>]
type TotalPeopleCustomProjectionEvents = 
    | Registered of PersonRegistered
    | NameChanged of PersonNameChanged
    | OrganizationRegistered of OrganizationRegistered
    interface ICustomProjectionEvents

[<HandlesStream(ProjectionStreamNames.TotalPeopleStream)>]
type TotalPeopleCustomProjection() =
    inherit Projection<TotalPeopleCustomProjectionEvents>()
    interface IAmHandledBy<TotalPeopleCustomProjectionHandler, TotalPeopleCustomProjectionEvents>
    interface IAmHandledBy<TotalMladensCustomProjectionHandler, TotalPeopleCustomProjectionEvents>

and TotalPeopleCustomProjectionHandler(store: INoSqlStore) =
    let Store = store
    
    interface IProjectionHandler<TotalPeopleCustomProjectionEvents> with
        member this.Handle(event: TotalPeopleCustomProjectionEvents, checkpoint: uint64) =
            task {
                match event with
                | TotalPeopleCustomProjectionEvents.Registered _ ->
                    do! this.Project(fun custom ->
                        { custom with
                            TotalPersons = custom.TotalPersons + 1 }).ConfigureAwait(false)

                | TotalPeopleCustomProjectionEvents.NameChanged _ -> ()

                | TotalPeopleCustomProjectionEvents.OrganizationRegistered _ ->
                    do! this.Project(fun custom ->
                        { custom with
                            TotalOrganizations = custom.TotalOrganizations + 1 }).ConfigureAwait(false)
            }
            
    member private this.Project(update: TotalPeople -> TotalPeople) =
        task {
            let id = "TotalPeople-All"
            let! doc = Store.LoadAsync<TotalPeople>([|id|]).ConfigureAwait(false)
            let custom = 
                if isNull (box doc.[id]) then 
                    { Id = id
                      TotalPersons = 0
                      TotalOrganizations = 0 }
                else doc.[id]
            let updated = update custom
            do! Store.StoreAsync(updated).ConfigureAwait(false)
        }

and TotalMladensCustomProjectionHandler(store: INoSqlStore) =
    let Store = store
    
    interface IProjectionHandler<TotalPeopleCustomProjectionEvents> with
        member this.Handle(event: TotalPeopleCustomProjectionEvents, checkpoint: uint64) =
            task {
                match event with
                | TotalPeopleCustomProjectionEvents.Registered ev ->
                    if (ev.Name = "Mladen") then
                        do! this.Project(fun totalPeople ->
                            { totalPeople with
                                TotalPersons = totalPeople.TotalPersons + 1 }).ConfigureAwait(false)

                | TotalPeopleCustomProjectionEvents.NameChanged ev ->
                   do! this.Project(fun totalPeople -> 
                       if (ev.Name = "Mladen") then
                           { totalPeople with TotalPersons = totalPeople.TotalPersons + 1 }
                       else
                           { totalPeople with TotalPersons = totalPeople.TotalPersons - 1 }
                       ).ConfigureAwait(false)

                | TotalPeopleCustomProjectionEvents.OrganizationRegistered _ ->
                    do! this.Project(fun totalPeople ->
                        { totalPeople with
                            TotalOrganizations = totalPeople.TotalOrganizations + 1 }).ConfigureAwait(false)
            }
            
    member private this.Project(update: TotalPeople -> TotalPeople) =
        task {
            let id = "TotalPeople-Mladens"
            let! doc = Store.LoadAsync<TotalPeople>([|id|]).ConfigureAwait(false)
            let custom = 
                if isNull (box doc.[id]) then 
                    { Id = id
                      TotalPersons = 0
                      TotalOrganizations = 0 }
                else doc.[id]
            let updated = update custom
            do! Store.StoreAsync(updated).ConfigureAwait(false)
        }

