namespace SI.Rosetta.Projections

open System
open Microsoft.Extensions.DependencyInjection
open SI.Rosetta.Common

type ProjectionHandlerFactory(provider: IServiceProvider) =
    interface IProjectionHandlerFactory with
        member this.Create<'TEvent when 'TEvent :> IAggregateEvents>(handlerType: Type) =
            let handlerInstance = provider.GetRequiredService(handlerType)
            handlerInstance :?> IProjectionHandler<'TEvent>