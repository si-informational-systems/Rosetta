-- The app's own schema for the Parcel read models, applied by the test as an app's migrations would be:
-- Rosetta maps the records onto these tables, and doesn't create or change them
create schema if not exists parcels;

-- ParcelRow, [<Table("parcels")>]: the parcel and its recipient
create table if not exists parcels.parcels (
    id text primary key,
    recipient text not null);

-- ParcelEventCount: how many events each parcel had, + 1 per event
create table if not exists parcels.parcel_event_counts (
    id text primary key,
    events int not null);

-- ParcelStop: every column type the mapping covers, with a composite key
create table if not exists parcels.parcel_stops (
    parcel_id text not null,
    stop int not null,
    arrived_at timestamptz not null,
    day date not null,
    fee numeric(12,2) not null,
    note text,
    floor int,
    tags text[] not null,
    attempts int[] not null,
    kind text not null,
    size text not null,
    handling jsonb not null,
    signed_by jsonb,
    primary key (parcel_id, stop));

-- For the startup check: a table without a key to upsert on
create table if not exists parcels.unkeyed_parcels (
    id text not null,
    recipient text not null);
