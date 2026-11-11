---
title: Custom Projections
description: Cross-Aggregate streams, generated and deployed to KurrentDB as JavaScript projections.
---

# Custom Projections

Custom Projections combine events from several Aggregates into one stream. Rosetta generates and deploys the KurrentDB JavaScript projection for you on startup, relinks it when its definition changes, and deletes it when the code no longer defines it.

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

On startup, Rosetta makes KurrentDB's projections the ones the code defines:

| When the projection | Rosetta | Logged |
|---|---|---|
| doesn't exist in KurrentDB (its name is `{StreamName without cp-}CustomProjection`) | **creates** it: continuous, emit enabled, on the V1 engine (V2 is still experimental in KurrentDB, and the engine can only be chosen at creation) | `CREATED` |
| has another query than the code's (e.g. a new event case), Faulted or not | **relinks** it: stops it, soft-deletes its `cp-` stream, updates the query, resets and starts it, so it links the **whole history** of its source streams again | `RELINKED` |
| is Faulted | re-submits the definition, which restarts it | `UPDATED: it was {status}` |
| has emit disabled | re-submits the definition | `UPDATED: emit was disabled` |
| is as the code defines it | leaves it alone | `is up to date` |
| was created by this application, and the code no longer defines it (removed, or renamed: a new name is a new projection) | **deletes** it, and soft-deletes its `cp-` stream | `DELETED` |

- The stored definition is read over the KurrentDB HTTP API, with the address, credentials and TLS settings of
  `KurrentDB:ConnectionString`. If it can't be read, the definition is re-submitted anyway and a Warning is logged.
- **Relinking** soft-deletes the stream: only the links go (KurrentDB truncates them away), the events they pointed to
  stay in their own streams. The Projections reading the stream are then [rebuilt](#the-projections-that-read-it).
- **Deleting** stops the projection, then deletes it over the HTTP API (`DELETE /projection/{name}`, with its checkpoint
  and state streams; the gRPC client can't delete projections), and soft-deletes its `cp-` stream. It happens also when
  the application no longer defines any Custom Projection: then once, and a failure is only a Warning (retried on the
  next start), outside the health check. An application that never defined one doesn't query KurrentDB's projections
  at all.

### Which projections an application owns

Each application records the Custom Projections it defined in the stream `RosettaCustomProjections-{owner}` (event
`CustomProjectionsDefined`, the latest 10 kept), and only ever deletes those: never another service's on the same
KurrentDB, nor one made by hand. The owner is `KurrentDB:CustomProjectionsOwner` when it's set, else the host's
`ApplicationName`, else the entry assembly's name. Every instance of an application shares its owner. Set it when two
applications on one KurrentDB have the same name, or before renaming the application (keeping the old name), since
another name is another owner.

The record starts with what the application defines on the first start that keeps one: projections left behind by
renames before that aren't known to be the application's, and stay. Delete those by hand (KurrentDB UI, or `DELETE /projection/{name}`), and
their streams with `DELETE /streams/{name}`.

### The Projections that read it

A Rosetta Projection with `[<HandlesStream("cp-…")>]` checks its stream when it starts, and is **rebuilt** when its
checkpoint is in links that were deleted (the stream was relinked or deleted), or past the stream's end (it isn't the
stream the read models were built from, e.g. after a rename): its `IProjectInto` read models are emptied and its
checkpoint reset to 0, as when its [`ProjectionVersion`](projections.md#rebuilding-a-projection) goes up. It logs a
Warning, "Projection {Name} is REBUILT: {reason}. …".

- It's safe across crashes: KurrentDB stores the stream's truncation, so the check finds it again.
- It must declare its read models with `IProjectInto` (or be marked `[<RebuildWithoutClearing>]`): registering it
  fails otherwise, as replaying the relinked stream over read models nothing emptied would count every event twice.
- Only `cp-` streams are checked.
- **Keep a `cp-` stream's Projections in the application that defines its Custom Projection**: there, the Custom
  Projections are deployed before any Projection starts. A Projection running in another service while the stream is
  relinked may read the new links as more events, without being rebuilt; stop it until the relinking application has
  started.
- **Renaming a `cp-` stream**: a consumer can't tell the new stream from the old one if the new one already holds at
  least as many links as its checkpoint when it starts. Bump the consumer's `[<ProjectionVersion>]` along with the
  rename.

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
- Only Event-Sourced Aggregates have streams: a state-based Aggregate's events never reach a Custom Projection.
