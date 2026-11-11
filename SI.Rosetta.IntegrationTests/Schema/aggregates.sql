-- The app's own schema for the state-based aggregates in PostgreSQL, applied by the test as an app's migrations would
-- be: Rosetta maps each state onto its table, and doesn't create or change them

-- ParcelAggregateState, by convention: the snake_case plural of the state type's name
create table if not exists parcel_aggregate_states (
    id text primary key,
    version bigint not null,
    recipient text not null);

-- RoutedParcelState: a list, an option and a map, as read models map them
create table if not exists routed_parcel_states (
    id text primary key,
    version bigint not null,
    recipients text[] not null,
    readdressed_at timestamptz,
    notes jsonb not null);

-- For the check: a state table without the column for one of its properties
create table if not exists unweighed_parcel_states (
    id text primary key,
    version bigint not null);
