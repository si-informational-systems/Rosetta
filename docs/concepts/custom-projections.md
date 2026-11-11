---
title: Custom Projections
description: Cross-Aggregate streams, generated and deployed to KurrentDB as JavaScript projections.
---

# Custom Projections

Custom Projections combine events from several Aggregates into one stream. Rosetta generates and deploys the KurrentDB JavaScript projection for you on startup.

```fsharp
[<AutoOpen>]
module ProjectionStreamNames =
    [<Literal>]
    let PersonStream = "$ce-People"
    [<Literal>]
    let OrganizationStream = "$ce-Organizations"
    [<Literal>]
    let TotalPeopleAndOrganizationsStream = "cp-TotalPeopleAndOrganizations"

[<CLIMutable>]
type Totals = {
    Id: string
    TotalPeople: int
    TotalOrganizations: int
}

[<CustomProjectionStream(ProjectionStreamNames.TotalPeopleAndOrganizationsStream)>]
type TotalPeopleAndOrganizationsEvents = 
    | PersonRegistered of PersonRegistered
    | OrganizationRegistered of OrganizationRegistered
    interface ICustomProjectionEvents

[<HandlesStream(ProjectionStreamNames.TotalPeopleAndOrganizationsStream)>]
type TotalPeopleAndOrganizationsProjection() =
    inherit Projection<TotalPeopleAndOrganizationsEvents>()
    interface IAmHandledBy<TotalPeopleAndOrganizationsProjectionHandler, TotalPeopleAndOrganizationsEvents>

and TotalPeopleAndOrganizationsProjectionHandler(store: INoSqlStore) =
    let TotalsId = "Totals-All"

    interface IProjectionHandler<TotalPeopleAndOrganizationsEvents> with
        member this.Handle(event, checkpoint) =
            task {
                match event with
                | TotalPeopleAndOrganizationsEvents.PersonRegistered _ ->
                    do! this.Project(fun totals -> { totals with TotalPeople = totals.TotalPeople + 1 })
                | TotalPeopleAndOrganizationsEvents.OrganizationRegistered _ ->
                    do! this.Project(fun totals -> { totals with TotalOrganizations = totals.TotalOrganizations + 1 })
            }

    member private this.Project(update: Totals -> Totals) =
        task {
            let! totals = store.LoadAsync<Totals>(TotalsId)
            let current =
                if isNull (box totals) then { Id = TotalsId; TotalPeople = 0; TotalOrganizations = 0 }
                else totals
            do! store.StoreAsync(update current)
        }
```

## Conventions the generator relies on

- The Custom Projection stream name **must start with `cp-`**.
- The cases reuse the Aggregates' event records (`PersonRegistered`, `OrganizationRegistered`).
- For every source Aggregate events DU (`PersonEvents`, `OrganizationEvents`), the Projections assembly must contain a `string` literal named `{AggregateName}Stream` (`PersonStream`, `OrganizationStream`) holding its category stream.

## How the JavaScript projections are deployed

- A projection is **created** when no KurrentDB projection with its name (`{StreamName without cp-}CustomProjection`) exists. It's created on the V1 projections engine: V2 is still experimental in KurrentDB, and the engine can only be chosen at creation.
- An existing projection is **updated only when needed**. Rosetta reads its stored definition over the KurrentDB HTTP API, using the address, credentials and TLS settings of `KurrentDB:ConnectionString`. It re-submits the definition when the query changed, when emit is disabled, or when the projection is Faulted (re-submitting restarts it). Otherwise the projection is left alone.
- If the stored definition can't be read, the definition is re-submitted anyway and a warning is logged.
- **Renaming** the stream creates a new projection. The old one is neither removed nor stopped, and keeps linking events into the old stream.
- A **changed definition** (e.g. a new event case) applies from the projection's last persisted checkpoint, not from the start of history. Older events of the new type are only partly linked, so reset the projection in KurrentDB, and rebuild the Read Model, if they matter.

## What the generated projection looks like

For the example above, Rosetta deploys a continuous KurrentDB projection named `TotalPeopleAndOrganizationsCustomProjection`
(emit enabled, V1 engine):

```javascript
fromStreams('$ce-People','$ce-Organizations').when({PersonRegistered: function(s,e){linkTo('cp-TotalPeopleAndOrganizations', e);return s;},OrganizationRegistered: function(s,e){linkTo('cp-TotalPeopleAndOrganizations', e);return s;}})
```

- The `when` keys are the cases' **stored event names** (the records' `[<EventType>]` names or type names), so a
  renamed record keeps being linked only if it has an `[<EventType>]`.
- KurrentDB must run with projections enabled (`KURRENTDB_RUN_PROJECTIONS=All`, `KURRENTDB_START_STANDARD_PROJECTIONS=true`
  for the `$ce-` category streams).
- Projections created this way don't track emitted streams, so deleting one doesn't delete its `cp-` stream: delete it
  with `DELETE /streams/{name}` over the HTTP API.
- Only Event-Sourced Aggregates have streams: a state-based Aggregate's events never reach a Custom Projection.
