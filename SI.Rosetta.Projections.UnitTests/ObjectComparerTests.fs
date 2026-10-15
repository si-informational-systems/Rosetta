namespace SI.Rosetta.Projections.UnitTests

open System
open Xunit
open SI.Rosetta.TestKit

type Payment = {
    Id: Guid
    Amount: decimal
    PaidOn: DateTime
    Currency: DayOfWeek
    Lines: string list
}

type ObjectComparerTests() =
    let Paid = {
        Id = Guid.Parse("6f1c1c43-8a0e-4c0b-9a3e-1b4f3c2d5e6a")
        Amount = 1.5m
        PaidOn = DateTime(2026, 10, 15)
        Currency = DayOfWeek.Monday
        Lines = [ "Fee" ]
    }

    [<Fact>]
    member this.``Equal objects have no differences``() =
        Assert.Equal(String.Empty, ObjectComparer.DeepCompare(Paid, { Paid with Lines = [ "Fee" ] }))

    [<Fact>]
    member this.``Different Guids are a difference``() =
        let difference = ObjectComparer.DeepCompare(Paid, { Paid with Id = Guid.NewGuid() })
        Assert.Contains("Id: Values differ", difference)

    [<Fact>]
    member this.``Decimals with the same scale but different values are a difference``() =
        let difference = ObjectComparer.DeepCompare(Paid, { Paid with Amount = 9.5m })
        Assert.Contains("Amount: Values differ", difference)

    [<Fact>]
    member this.``Different dates, enums and list items are differences``() =
        let difference =
            ObjectComparer.DeepCompare(Paid, { Paid with PaidOn = DateTime(2026, 10, 16); Currency = DayOfWeek.Friday; Lines = [ "Tax" ] })
        Assert.Contains("PaidOn: Values differ", difference)
        Assert.Contains("Currency: Values differ", difference)
        Assert.Contains("Lines[0]: Values differ", difference)
