---
title: Configuration
description: The configuration sections each store needs.
---

# Configuration

Rosetta reads its settings from `IConfiguration` (`appsettings.json`, environment variables, the command line…). Only
the sections of the stores you register are needed; a required one that's missing fails at registration with a
message naming the key.

```json
{
  "KurrentDB": {
    "ConnectionString": "kurrentdb://localhost:2113?tls=false"
  },
  "RavenDB": {
    "Urls": "http://localhost:8080",
    "DatabaseName": "MyApp",
    "CertificatePath": "certs/client.pfx",
    "CertificatePassword": "…"
  },
  "MongoDB": {
    "ConnectionString": "mongodb://localhost:27017/MyApp?replicaSet=rs0"
  },
  "PostgreSQL": {
    "ConnectionString": "Host=localhost;Port=5432;Database=MyApp;Username=app;Password=…",
    "Schema": "public"
  },
  "Outbox": {
    "CollectionName": "Outbox",
    "EventTTLMinutes": 10080
  }
}
```

## KurrentDB

- `ConnectionString` (required): a `kurrentdb://` connection string.
- A service has **one `KurrentDBClient`** (and one `KurrentDBProjectionManagementClient`), shared by its Aggregates and
  every Projection. It's created on first use, so a service starts while KurrentDB is unreachable; the calls that need
  it fail until it's back, and Projections retry. To configure the client yourself, register your own `KurrentDBClient`
  singleton before Rosetta's registration.
- The HTTP API (for reading Custom Projection definitions) uses the same address, credentials and TLS settings.
- A single KurrentDB node is free to run in production from 26.2; clusters need a license.

## RavenDB

- `Urls` (required): one or more URLs separated by `;`.
- `DatabaseName` (required). A missing database is created.
- `CertificatePath`, `CertificatePassword` (optional): a client certificate for a secured server.
- One `IDocumentStore` is registered and shared by the Aggregates, the Outbox and the Projections; one the app
  registered first is kept (register your own to set RavenDB conventions).
- Unlike KurrentDB's client, the `IDocumentStore` is created **at registration**: it connects, creates the database
  when it's missing, and blocks until RavenDB answers. A RavenDB that's down fails the application's startup.

## MongoDB

Either the whole connection string:

```json
{ "MongoDB": { "ConnectionString": "mongodb+srv://user:pass@cluster0.example.net/MyApp?retryWrites=true" } }
```

or its parts:

```json
{ "MongoDB": { "Host": "localhost", "Port": "27017", "Username": "app", "Password": "…", "AuthSource": "admin", "DatabaseName": "MyApp" } }
```

- `ConnectionString` replaces `Host`, `Port`, `Username`, `Password` and `AuthSource`; when `DatabaseName` isn't set,
  the database comes from its path.
- The credentials are escaped, so they may contain `@`, `:`, `/`, `?`, `#` or `%`. They're applied only when both
  `Username` and `Password` are set.
- The `IMongoClient` is created at registration (it connects lazily), and an `IMongoClient` or `IMongoDatabase` the app
  registered first is kept.
- **Projections need a replica set**, as each event is committed in a transaction; so does the Outbox's transaction with
  state-based Aggregates. A single-node replica set is enough: start `mongod --replSet rs0`, run `rs.initiate()` once,
  and connect with `replicaSet=rs0` (or `directConnection=true`).
- Rosetta registers BSON conventions process-wide: camelCase element names, extra elements ignored, enums as strings,
  and serializers for F# **lists** (arrays), **maps** (a sub-document, or `[key, value]` pairs for non-string keys),
  **unions** (`{ "case": "Crate", "fields": [2, 3] }`) and options (`{ "value": … }` or `null`).

## PostgreSQL

- `ConnectionString` (required). Rosetta keeps its own connection pool for it.
- `Schema` (optional, `public` unless set): the schema of the read model tables that don't name theirs.
- Rosetta creates a missing database (needs `CREATEDB`) and its `rosetta.checkpoints` table (needs `CREATE` on the
  database). Your migrations create the read model tables. See [PostgreSQL read models](../concepts/postgresql.md).

## Outbox

All optional; see [Outbox](../concepts/outbox.md#settings).

| Key | Default |
|---|---|
| `Outbox:CollectionName` | `Outbox` |
| `Outbox:EventTTLMinutes` | kept until deleted |
| `Outbox:ExpirationFrequencyMinutes` | `2160` (RavenDB only) |

Every key with its default is in the [Configuration reference](../reference/configuration.md).
