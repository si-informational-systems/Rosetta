namespace SI.Rosetta.Common

[<AutoOpen>]
module Messages =
    type ICommands = interface end
    type IEvents = interface end

    type IAggregateCommands =
        inherit ICommands

    type IAggregateEvents =
        inherit IEvents

    type ICustomProjectionEvents =
        inherit IEvents
