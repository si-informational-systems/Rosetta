namespace SI.Rosetta.Common

[<AutoOpen>]
module Messages =
    type IAggregateCommands = interface end

    type IAggregateEvents = interface end

    type ICustomProjectionEvents =
        inherit IAggregateEvents
