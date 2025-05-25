namespace SI.Rosetta.Projections

open System.Collections.Generic
open System.Threading.Tasks

type IProjectionsStore =
    abstract member StoreAsync<'T> : doc: 'T -> Task
    abstract member StoreInUnitOfWorkAsync<'T> : docs: 'T[] -> Task
    abstract member LoadAsync<'T when 'T : not struct> : ids: string[] -> Task<Dictionary<string, 'T>>
    abstract member DeleteAsync: id: string -> Task
    abstract member DeleteInUnitOfWorkAsync: ids: string[] -> Task

type INoSqlStore =
    inherit IProjectionsStore

type ISqlStore =
    inherit IProjectionsStore 