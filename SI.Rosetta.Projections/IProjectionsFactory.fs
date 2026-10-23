namespace SI.Rosetta.Projections

open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open System
open SI.Rosetta.Common

type IProjectionsFactory =
    abstract member CreateFromAssemblyAsync: projectionsAssembly: Assembly -> Task<IList<IProjection>>
    abstract member CreateAsync: t: Type -> Task<IProjection>
    /// The projection projectionType declares, of 'TEvent: created without reflection over 'TEvent
    abstract member CreateAsync<'TEvent when 'TEvent :> IAggregateEvents> : projectionType: Type -> Task<IProjection>
