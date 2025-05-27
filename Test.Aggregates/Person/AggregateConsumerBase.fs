namespace TestFSharp

open Microsoft.Extensions.Logging
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

[<AbstractClass>]
type AggregateConsumerBase<'TCommands when 'TCommands :> IAggregateCommands and 'TCommands : not struct>() =
    member this.TryHandle(message: 'TCommands, svc: IAggregateHandler, logger: ILogger) = 
        task {
            try
                do! svc.ExecuteAsync(message).ConfigureAwait(false)
                for event in svc.GetPublishedEvents() do
                    logger.LogInformation("Published event: {Event}", event)
            with
            | :? DomainException as ex ->
                logger.LogError(ex.Message)
            | ex ->
                logger.LogError(ex.Message)
                raise ex
        }

