namespace SI.Rosetta.Projections

open System.Collections.Generic
open System.Reflection
open System.Threading.Tasks
open System

type IProjectionsFactory =
    abstract member CreateFromAssemblyAsync: projectionsAssembly: Assembly -> Task<IList<IProjection>>
    abstract member CreateAsync: t: Type -> Task<IProjection>
