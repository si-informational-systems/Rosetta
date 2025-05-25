namespace SI.Rosetta.Projections.EventStore

open System
open System.Text

module EventStoreProjectionBuilder =
    let private IsNotFirstElement i = i > 0

    let private CreateSourceStreams (parameters: EventStoreProjectionParameters) =
        let sb = StringBuilder()
        parameters.AggregateEventsStreamNames
        |> List.iteri (fun i name ->
            if IsNotFirstElement i then
                sb.Append "," |> ignore
            sb.Append(sprintf "'%s'" name) |> ignore)
        sb.ToString()

    let private CreateBody (parameters: EventStoreProjectionParameters) =
        let sb = StringBuilder()
        parameters.EventsToInclude
        |> Array.iteri (fun i eventType ->
            if IsNotFirstElement i then
                sb.Append "," |> ignore
            sb.Append(sprintf "%s: function(s,e){linkTo('%s', e);return s;}"
                eventType.Name parameters.DestinationStreamName) |> ignore)
        sb.ToString()

    let BuildProjectionDefinition (parameters: EventStoreProjectionParameters): EventStoreProjection =
        let body = 
            sprintf "fromStreams(%s).when({%s})" 
                (CreateSourceStreams parameters)
                (CreateBody parameters)
        let result : EventStoreProjection = { 
            Name = parameters.ProjectionName 
            Source = body 
        } 
        result