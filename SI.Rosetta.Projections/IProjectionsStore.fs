namespace SI.Rosetta.Projections

open System.Collections.Generic
open System.Threading.Tasks

type IProjectionsStore =
    abstract member StoreAsync<'T when 'T : not struct> : doc: 'T -> Task
    abstract member StoreInUnitOfWorkAsync<'T when 'T : not struct> : docs: 'T[] -> Task
    abstract member LoadAsync<'T when 'T : not struct> : id: obj -> Task<'T>
    abstract member LoadManyAsync<'T when 'T : not struct> : ids: obj[] -> Task<Dictionary<obj, 'T>>
    abstract member DeleteAsync<'T when 'T : not struct>: id: obj -> Task
    abstract member DeleteInUnitOfWorkAsync<'T when 'T : not struct>: ids: obj[] -> Task

/// The document stores: RavenDB and MongoDB
type INoSqlStore =
    inherit IProjectionsStore

/// The SQL store: PostgreSQL. The same typed calls, by key, on rows of the read model types' tables
type ISqlStore =
    inherit IProjectionsStore
