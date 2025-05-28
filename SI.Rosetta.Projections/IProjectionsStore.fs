namespace SI.Rosetta.Projections

open System.Collections.Generic
open System.Threading.Tasks

type IProjectionsStore =
    abstract member StoreAsync<'T when 'T : not struct> : doc: 'T -> Task
    abstract member StoreInUnitOfWorkAsync<'T when 'T : not struct> : docs: 'T[] -> Task
    abstract member LoadAsync<'T when 'T : not struct> : ids: obj[] -> Task<Dictionary<obj, 'T>>
    abstract member DeleteAsync: id: obj -> Task
    abstract member DeleteInUnitOfWorkAsync: ids: obj[] -> Task

type INoSqlStore =
    inherit IProjectionsStore

type ISqlStore =
    inherit IProjectionsStore 