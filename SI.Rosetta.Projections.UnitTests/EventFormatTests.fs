namespace SI.Rosetta.Projections.UnitTests

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open Xunit
open SI.Rosetta.Common

/// An event record with every common kind of field, to pin down the format events are stored in
module EventFormat =
    type Email = Email of string
    type Status = | Active | Suspended of reason: string
    [<Flags>]
    type Perms = None = 0 | Read = 1 | Write = 2
    [<CLIMutable>]
    type Address = { Street: string; City: string }

    type Everything = {
        Id: string; NullString: string; Nickname: string option; NoNickname: string option; OldAddress: Address option
        VNick: string voption; Born: DateTime; BornUnspecified: DateTime; At: DateTimeOffset; Span: TimeSpan
        Balance: decimal; Ratio: float; Big: int64; Flag: bool; Letter: char; Bytes: byte[]; Key: Guid
        Day: DayOfWeek; Perms: Perms; Tags: string list; Numbers: int[]; Labels: Set<string>; Scores: Map<string, int>
        ByNumber: Map<int, string>; Pair: string * int; Address: Address; Email: Email; Status: Status; Suspension: Status
        MaybeCount: Nullable<int>; NoCount: Nullable<int>; Link: Uri }

    [<RequireQualifiedAccess>]
    type Events =
        | Happened of Everything
        interface IAggregateEvents

    let Value = {
        Id = "Things-1"; NullString = null; Nickname = Some "Johnny"; NoNickname = None; OldAddress = Some { Street = "Old 2"; City = "Beograd" }
        VNick = ValueSome "J"; Born = DateTime(1990, 5, 17, 8, 30, 0, DateTimeKind.Utc); BornUnspecified = DateTime(1990, 5, 17, 8, 30, 0, 123)
        At = DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.FromHours 2.0); Span = TimeSpan.FromMinutes 90.0; Balance = 12.50m; Ratio = 0.1
        Big = 9007199254740993L; Flag = true; Letter = 'x'; Bytes = [| 1uy; 2uy; 255uy |]; Key = Guid.Parse "6f1c1c43-8a0e-4c0b-9a3e-1b4f3c2d5e6a"
        Day = DayOfWeek.Friday; Perms = Perms.Read ||| Perms.Write; Tags = [ "a"; "b" ]; Numbers = [| 1; 2 |]; Labels = Set [ "x"; "y" ]
        Scores = Map [ ("x", 1) ]; ByNumber = Map [ (1, "one") ]; Pair = ("p", 2); Address = { Street = "Main 1"; City = "Novi Sad" }
        Email = Email "j@x.io"; Status = Active; Suspension = Suspended "audit"; MaybeCount = Nullable 3; NoCount = Nullable(); Link = Uri "https://x.io/a" }

    /// Value as Rosetta 1.25 and earlier stored it (Newtonsoft; the decimal went through a double, so 12.50 became 12.5)
    let StoredBy125 =
        """{"Id":"Things-1","NullString":null,"Nickname":{"Case":"Some","Fields":["Johnny"]},"NoNickname":null,"OldAddress":{"Case":"Some","Fields":[{"Street":"Old 2","City":"Beograd"}]},"VNick":{"Case":"ValueSome","Fields":["J"]},"Born":"1990-05-17T08:30:00Z","BornUnspecified":"1990-05-17T08:30:00.123","At":"2026-10-15T08:30:00+02:00","Span":"01:30:00","Balance":12.5,"Ratio":0.1,"Big":9007199254740993,"Flag":true,"Letter":"x","Bytes":"AQL/","Key":"6f1c1c43-8a0e-4c0b-9a3e-1b4f3c2d5e6a","Day":"Friday","Perms":"Read, Write","Tags":["a","b"],"Numbers":[1,2],"Labels":["x","y"],"Scores":{"x":1},"ByNumber":{"1":"one"},"Pair":{"Item1":"p","Item2":2},"Address":{"Street":"Main 1","City":"Novi Sad"},"Email":{"Case":"Email","Fields":["j@x.io"]},"Status":{"Case":"Active"},"Suspension":{"Case":"Suspended","Fields":["audit"]},"MaybeCount":3,"NoCount":null,"Link":"https://x.io/a"}"""

    /// Value as Rosetta stores it now: F# options as plain values
    let Stored =
        """{"Id":"Things-1","NullString":null,"Nickname":"Johnny","NoNickname":null,"OldAddress":{"Street":"Old 2","City":"Beograd"},"VNick":{"Case":"ValueSome","Fields":["J"]},"Born":"1990-05-17T08:30:00Z","BornUnspecified":"1990-05-17T08:30:00.123","At":"2026-10-15T08:30:00+02:00","Span":"01:30:00","Balance":12.50,"Ratio":0.1,"Big":9007199254740993,"Flag":true,"Letter":"x","Bytes":"AQL/","Key":"6f1c1c43-8a0e-4c0b-9a3e-1b4f3c2d5e6a","Day":"Friday","Perms":"Read, Write","Tags":["a","b"],"Numbers":[1,2],"Labels":["x","y"],"Scores":{"x":1},"ByNumber":{"1":"one"},"Pair":{"Item1":"p","Item2":2},"Address":{"Street":"Main 1","City":"Novi Sad"},"Email":{"Case":"Email","Fields":["j@x.io"]},"Status":{"Case":"Active"},"Suspension":{"Case":"Suspended","Fields":["audit"]},"MaybeCount":3,"NoCount":null,"Link":"https://x.io/a"}"""

    /// Value with its F# options written as Rosetta 1.25 did (OptionFormat.Newtonsoft)
    let StoredWithNewtonsoftOptions =
        """{"Id":"Things-1","NullString":null,"Nickname":{"Case":"Some","Fields":["Johnny"]},"NoNickname":null,"OldAddress":{"Case":"Some","Fields":[{"Street":"Old 2","City":"Beograd"}]},"VNick":{"Case":"ValueSome","Fields":["J"]},"Born":"1990-05-17T08:30:00Z","BornUnspecified":"1990-05-17T08:30:00.123","At":"2026-10-15T08:30:00+02:00","Span":"01:30:00","Balance":12.50,"Ratio":0.1,"Big":9007199254740993,"Flag":true,"Letter":"x","Bytes":"AQL/","Key":"6f1c1c43-8a0e-4c0b-9a3e-1b4f3c2d5e6a","Day":"Friday","Perms":"Read, Write","Tags":["a","b"],"Numbers":[1,2],"Labels":["x","y"],"Scores":{"x":1},"ByNumber":{"1":"one"},"Pair":{"Item1":"p","Item2":2},"Address":{"Street":"Main 1","City":"Novi Sad"},"Email":{"Case":"Email","Fields":["j@x.io"]},"Status":{"Case":"Active"},"Suspension":{"Case":"Suspended","Fields":["audit"]},"MaybeCount":3,"NoCount":null,"Link":"https://x.io/a"}"""

    /// The JSON without its formatting, to compare it
    let Compact (json: string) = JsonNode.Parse(json).ToJsonString()

    let Read<'TEvents when 'TEvents :> IAggregateEvents> (typeName: string) (json: string) =
        match EventEncoding.Decode<'TEvents> [] typeName json with
        | Ok ev -> ev
        | Error unknown -> failwithf "%s has no case" unknown

/// Everything as a C# record reads it: nullable fields for the options, and no F# converters
[<CLIMutable>]
type EverythingInCSharp = {
    Id: string; Nickname: string; NoNickname: string; OldAddress: EventFormat.Address
    Balance: decimal; Key: Guid; Day: DayOfWeek; Tags: string[]; MaybeCount: Nullable<int>; NoCount: Nullable<int> }

type PaymentReceived = { Id: string; Amount: decimal; Rate: decimal }
type NoteAdded = { Id: string; Text: string }

[<RequireQualifiedAccess>]
type LedgerEvents =
    | PaymentReceived of PaymentReceived
    | NoteAdded of NoteAdded
    interface IAggregateEvents

type EventFormatTests() =
    [<Fact>]
    member this.``Events are written in Rosetta's stored format``() =
        let typeName, json = EventEncoding.Encode(EventFormat.Events.Happened EventFormat.Value)

        Assert.Equal("Everything", typeName)
        Assert.Equal(EventFormat.Compact EventFormat.Stored, EventFormat.Compact json)

    [<Fact>]
    member this.``Events Rosetta 1.25 and earlier stored are read the same``() =
        Assert.Equal(EventFormat.Events.Happened EventFormat.Value, EventFormat.Read<EventFormat.Events> "Everything" EventFormat.StoredBy125)

    [<Fact>]
    member this.``Decimals keep their precision and scale``() =
        let payment = LedgerEvents.PaymentReceived { Id = "Ledgers-1"; Amount = 1234567890.123456789012345678m; Rate = 12.50m }

        let typeName, json = EventEncoding.Encode payment

        Assert.Contains("1234567890.123456789012345678", json)
        Assert.Contains("12.50", json)
        Assert.Equal(payment, EventFormat.Read<LedgerEvents> typeName json)

    [<Fact>]
    member this.``Date-like strings are stored as written``() =
        let note = LedgerEvents.NoteAdded { Id = "Ledgers-1"; Text = "2026-10-15T08:30:00.000Z" }

        let typeName, json = EventEncoding.Encode note

        match EventFormat.Read<LedgerEvents> typeName json with
        | LedgerEvents.NoteAdded read -> Assert.Equal("2026-10-15T08:30:00.000Z", read.Text)
        | other -> failwithf "Read as %A" other

    [<Fact>]
    member this.``Events with plain options are read``() =
        Assert.Equal(EventFormat.Events.Happened EventFormat.Value, EventFormat.Read<EventFormat.Events> "Everything" EventFormat.Stored)

    [<Fact>]
    member this.``Options can still be written as Rosetta 1.25 did``() =
        let _, json = EventEncoding.EncodeWith OptionFormat.Newtonsoft (EventFormat.Events.Happened EventFormat.Value)

        Assert.Equal(EventFormat.Compact EventFormat.StoredWithNewtonsoftOptions, EventFormat.Compact json)
        Assert.Equal(EventFormat.Events.Happened EventFormat.Value, EventFormat.Read<EventFormat.Events> "Everything" json)

    [<Fact>]
    member this.``A C# reader reads the events Rosetta writes``() =
        let _, json = EventEncoding.Encode(EventFormat.Events.Happened EventFormat.Value)
        let csharp = JsonSerializerOptions()
        csharp.Converters.Add(JsonStringEnumConverter())

        let read = JsonSerializer.Deserialize<EverythingInCSharp>(json, csharp)

        Assert.Equal(
            { Id = "Things-1"; Nickname = "Johnny"; NoNickname = null; OldAddress = { Street = "Old 2"; City = "Beograd" }
              Balance = 12.50m; Key = EventFormat.Value.Key; Day = DayOfWeek.Friday; Tags = [| "a"; "b" |]
              MaybeCount = Nullable 3; NoCount = Nullable() },
            read)
