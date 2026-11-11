---
title: Operations
description: Running the infrastructure Rosetta needs, locally and in production, and what happens when it fails.
---

# Operations

## A local stack

```yaml title="docker-compose.yml"
name: myapp

services:
  kurrentdb:
    image: kurrentplatform/kurrentdb:26.2
    environment:
      KURRENTDB_INSECURE: "true"
      KURRENTDB_RUN_PROJECTIONS: All                # Rosetta deploys Custom (cp-) JS projections
      KURRENTDB_START_STANDARD_PROJECTIONS: "true"  # $by_category feeds the $ce-* streams Projections subscribe to
      KURRENTDB_ENABLE_ATOM_PUB_OVER_HTTP: "true"   # read and append events over HTTP (curl)
      KURRENTDB_TELEMETRY_OPTOUT: "true"
    ports: ["2113:2113"]
    volumes:
      - kurrentdb-data:/var/lib/kurrentdb
      - kurrentdb-logs:/var/log/kurrentdb

  ravendb:
    image: ravendb/ravendb:7.2-latest
    environment:
      RAVEN_Setup_Mode: None
      RAVEN_License_Eula_Accepted: "true"
      RAVEN_Security_UnsecuredAccessAllowed: PublicNetwork
      RAVEN_ServerUrl: http://0.0.0.0:8080
      RAVEN_PublicServerUrl: http://localhost:8080
    ports: ["8080:8080"]
    volumes: [ravendb-data:/var/lib/ravendb/data]

  mongodb:
    image: mongo:8
    command: ["--replSet", "rs0", "--bind_ip_all"]
    ports: ["27017:27017"]
    healthcheck:   # initiates the single-node replica set once
      test: mongosh --quiet --eval "try { rs.status().ok } catch (e) { rs.initiate({_id:'rs0',members:[{_id:0,host:'localhost:27017'}]}).ok }"
      interval: 5s

  postgres:
    image: postgres:18-alpine
    environment:
      POSTGRES_USER: app
      POSTGRES_PASSWORD: app
      POSTGRES_DB: myapp
    ports: ["5432:5432"]

volumes:
  kurrentdb-data:
  kurrentdb-logs:
  ravendb-data:
```

Keep only the services you use. KurrentDB's UI is on http://localhost:2113, RavenDB Studio on http://localhost:8080.
These settings are **unsecured, for development only**.

## KurrentDB

- **Projections must be on** (`RUN_PROJECTIONS=All`) and the standard projections started: `$by_category` builds the
  `$ce-{Category}` streams Projections subscribe to, and Custom Projections run on the projections engine.
- A single node is free to run in production from 26.2; clusters need a license.
- **Outages**: Aggregates fail their commands while KurrentDB is unreachable (the client reconnects on its own).
  Projections resubscribe with 1, 2, 4, 8, 16, 30 s backoff; a restart of a single node is usually over by the 5th
  attempt (about 30 s), before the Projection is Faulted.
- **Append a test event** over HTTP (with AtomPub enabled):

    ```shell
    curl -X POST http://localhost:2113/streams/People-1 \
      -H "Content-Type: application/vnd.eventstore.events+json" \
      -d '[{"eventId":"5b4e5a1c-6f0e-4c39-9f3e-7f1c1d1e2a01","eventType":"PersonRegistered",
            "data":{"Id":"People-1","Name":"Ada"},
            "metadata":{"DiscriminatedUnionCaseType":"PersonRegistered"}}]'
    ```

    Rosetta reads an event by its `DiscriminatedUnionCaseType` metadata, so that's the one header it needs.
- **Custom Projections** are created, relinked and deleted by the application that defines them (see
  [Custom Projections](../concepts/custom-projections.md#how-the-javascript-projections-are-deployed)). One left behind
  from before an application kept its `RosettaCustomProjections-{owner}` record is deleted by hand: stop it, then
  `DELETE /projection/{name}?deleteCheckpointStream=yes&deleteStateStream=yes`, and `DELETE /streams/cp-…`.
- **Snapshots** live in `RosettaSnapshots-{Id}` streams, keeping only the latest. One that's missing or doesn't fit only
  means the Aggregate loads from its whole stream, until the next is written.

## RavenDB

- One `IDocumentStore` per service, created at startup: RavenDB must be reachable when the service starts.
- Rosetta creates a missing database.
- The Outbox publisher queries the Outbox over HTTP like everything else. **Data subscriptions** of your own connect
  over TCP to the server's advertised `PublicServerUrl.Tcp`: it must be reachable from the service.
- **Outbox expiry** needs Document Expiration; the Outbox turns it on with a frequency every licence allows (36 h). See
  [Outbox](../concepts/outbox.md#expiry).

## MongoDB

- **A replica set** for Projections and for the Outbox's transaction with state-based Aggregates. One node is enough:
  `mongod --replSet rs0`, `rs.initiate()` once, connect with `replicaSet=rs0` or `directConnection=true`.
- Projections check this at startup and stop with a message saying how to set it up when it's a standalone server.

## PostgreSQL

- The connection's user needs `CREATE` on the database (for the `rosetta` schema, `rosetta.checkpoints`,
  `rosetta.outbox` and `rosetta.outbox_leases`), and `CREATEDB` if Rosetta should create the database. An existing
  Outbox table also gets its `published_at` column (`alter table`), so the user must own it.
- Run the app's migrations before the Projections and the Aggregates; until the tables match, Projections retry and
  log what's missing, and commands on a state-based Aggregate fail with what's missing.
- Rosetta keeps its own connection pool, one per host: size `Maximum Pool Size` for the Projections running at once
  and the commands in flight.
- **Outbox expiry**: PostgreSQL has no TTL, so the Outbox deletes its expired rows itself, at most every
  `ExpirationFrequencyMinutes`. See [Outbox](../concepts/outbox.md#expiry).

## Health and alerting

```fsharp
builder.Services.AddHealthChecks().AddRosettaProjections() |> ignore
app.MapHealthChecks("/health") |> ignore
```

| Signal | Alert when |
|---|---|
| health check `rosetta-projections` | Unhealthy (a Projection Faulted or Stopped) |
| gauge `rosetta.projections.unhealthy` | sum > 0 |
| counter `rosetta.projection.faults` | increases |
| counter `rosetta.commands` with `rosetta.outcome=failed` | rate > 0 (rejections are `rejected`, not failures) |
| counter `rosetta.outbox.published` | flat while `rosetta.outbox.events` grows (the publisher is stuck) |

The logs say what happened to each Projection: `STARTED`, `RECOVERED`, `SKIPPED`, `DROPPED` (Error), `FAULTED`,
`STOPPED` (Critical), `CANCELED`. The Outbox publisher logs each failure as an Error, the 5th in a row as Critical, and
`RECOVERED`; it isn't in the health check.

## Deployments

- **Readers before writers**: deploy the services that read a new event (Projections, consumers) before the ones that
  write it (Aggregates).
- **Rebuilds** happen on start when a Projection's `ProjectionVersion` went up, or its `cp-` stream was relinked (its
  Custom Projection's definition changed): its read models are partial until it catches up.
- **The Outbox publisher** runs in every instance, and one at a time publishes. When that instance stops (a
  deployment), it hands its lease over and another takes over within half a lease (15 s); after a crash, once the
  lease expires (up to 45 s). Events are published at least once.
- **An older deployment** still running a Projection whose version is lower than the stored one refuses to start it
  (Stopped), so two versions never write the same read models.
- **One instance per Projection.** A Projection has one checkpoint: running it in two instances would handle every event
  twice. Spread Projections over hosts to scale.

## Readable logs locally

```shell
Logging__Console__FormatterName=simple
Logging__Console__FormatterOptions__TimestampFormat="HH:mm:ss.fff "
Logging__Console__FormatterOptions__SingleLine=true
```
