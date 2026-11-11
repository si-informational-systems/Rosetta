---
title: Configuration reference
description: Every configuration key Rosetta reads, with defaults, and every default it applies.
---

# Configuration reference

## Keys

| Key | Required | Default | |
|---|---|---|---|
| `KurrentDB:ConnectionString` | with KurrentDB | — | `kurrentdb://host:2113?tls=false`; also used for the HTTP API |
| `RavenDB:Urls` | with RavenDB | — | `;`-separated |
| `RavenDB:DatabaseName` | with RavenDB | — | created when missing |
| `RavenDB:CertificatePath` | no | — | a PKCS#12 client certificate |
| `RavenDB:CertificatePassword` | no | — | |
| `MongoDB:ConnectionString` | with MongoDB, or `Host` | — | replaces `Host`, `Port`, `Username`, `Password`, `AuthSource` |
| `MongoDB:Host` | with MongoDB, or `ConnectionString` | — | |
| `MongoDB:Port` | no | MongoDB's | |
| `MongoDB:Username`, `MongoDB:Password` | no | — | applied when both are set; escaped |
| `MongoDB:AuthSource` | no | — | |
| `MongoDB:DatabaseName` | with MongoDB | the connection string's path | |
| `PostgreSQL:ConnectionString` | with PostgreSQL | — | Npgsql format |
| `PostgreSQL:Schema` | no | `public` | for tables without `[<Table(Schema = …)>]` |
| `Outbox:CollectionName` | no | `Outbox` | |
| `Outbox:EventTTLMinutes` | no | kept until deleted | positive number |
| `Outbox:ExpirationFrequencyMinutes` | no | `2160` (36 h) | RavenDB only |

## Defaults in code

| What | Value | Change it |
|---|---|---|
| Projection resubscription backoff | 1, 2, 4, 8, 16, then 30 s | register a `ProjectionRetries` before `UseProjectionsWith` |
| Failures in a row before Faulted | 5 | `ProjectionRetries.FailuresBeforeFaulted` |
| PostgreSQL preparation retries (database booting) | 1, 2, 4, 8, 16, 30 s (about a minute) | — |
| Store retries outside a Projection's transaction | RavenDB 3 × 100 ms, MongoDB 3 × 50 ms, PostgreSQL 3 × 100 ms, on transient errors | — |
| MongoDB commit retries | 3 attempts on `UnknownTransactionCommitResult` | — |
| RavenDB rebuild (delete by query) timeout | 5 min | — |
| KurrentDB HTTP API timeout (Custom Projection definitions) | 10 s | — |
| Health check name | `rosetta-projections` | `AddRosettaProjections(name, tags)` |
| Option format written in events | `Plain` (the value or `null`) | `EventEncoding.WriteOptionsAs <- OptionFormat.Newtonsoft` |

## Names Rosetta derives

| Name | From | Example |
|---|---|---|
| Aggregate name | Aggregate type name without "Aggregate" | `PersonAggregate` → `Person` |
| Missing-Aggregate error | `{Aggregate}DoesNotExist` | `PersonDoesNotExist` |
| KurrentDB stream | the Aggregate's `Id` | `People-1` |
| State-based collection (MongoDB) | pluralized state type name | `PersonAggregateState` → `PersonAggregateStates` |
| Projection name | Projection type name without "Projection" | `PersonProjection` → `Person` |
| Checkpoint id | `Checkpoints-{Projection}` | `Checkpoints-Person` |
| Document read model collection (MongoDB) | pluralized type name | `Person` → `People` |
| PostgreSQL table | snake_case plural of the type, or `[<Table>]` | `AreaHour` → `area_hours` |
| PostgreSQL column | snake_case of the field, or `[<Column>]` | `SessionDate` → `session_date` |
| PostgreSQL checkpoints | `rosetta.checkpoints` | |
| Custom Projection in KurrentDB | stream without `cp-` + `CustomProjection` | `cp-Totals` → `TotalsCustomProjection` |
| Stream constant for a Custom Projection | `{Aggregate}Stream` | `PersonStream` |
