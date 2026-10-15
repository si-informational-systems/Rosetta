namespace SI.Rosetta.Projections.KurrentDB

open System
open System.Text
open SI.Rosetta.Common

module KurrentDBProjectionBuilder =
    let private IsNotFirstElement i = i > 0

    let private CreateSourceStreams (parameters: KurrentDBProjectionParameters) =
        let sb = StringBuilder()
        parameters.AggregateEventsStreamNames
        |> List.iteri (fun i name ->
            if IsNotFirstElement i then
                sb.Append "," |> ignore
            sb.Append(sprintf "'%s'" name) |> ignore)
        sb.ToString()

    let private CreateBody (parameters: KurrentDBProjectionParameters) =
        let sb = StringBuilder()
        parameters.EventsToInclude
        |> Array.iteri (fun i eventType ->
            if IsNotFirstElement i then
                sb.Append "," |> ignore
            sb.Append(sprintf "%s: function(s,e){linkTo('%s', e);return s;}"
                (EventEncoding.TypeNameOf eventType) parameters.DestinationStreamName) |> ignore)
        sb.ToString()

    let BuildProjectionDefinition (parameters: KurrentDBProjectionParameters): KurrentDBProjection =
        let body = 
            sprintf "fromStreams(%s).when({%s})" 
                (CreateSourceStreams parameters)
                (CreateBody parameters)
        let result : KurrentDBProjection = { 
            Name = parameters.ProjectionName 
            Source = body 
        } 
        result