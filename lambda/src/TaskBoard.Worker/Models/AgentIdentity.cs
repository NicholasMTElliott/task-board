namespace TaskBoard.Worker.Models;

/// <summary>
/// Human-friendly identity for an agent process instance.
/// Generated once at startup; threaded through logging and comments.
/// </summary>
public sealed record AgentIdentity(string FirstName, string LastName, string MachineName)
{
    /// <summary>Display format: "FirstName LastName on MachineName"</summary>
    public string DisplayName => $"{FirstName} {LastName} on {MachineName}";

    public static AgentIdentity Generate()
    {
        var first = FirstNames[Random.Shared.Next(FirstNames.Length)];
        var last = LastNames[Random.Shared.Next(LastNames.Length)];
        var machine = Environment.MachineName;
        return new AgentIdentity(first, last, machine);
    }

    public static IReadOnlyList<string> AvailableFirstNames => FirstNames;
    public static IReadOnlyList<string> AvailableLastNames => LastNames;

    private static readonly string[] FirstNames =
    [
        "Ada", "Alan", "Alice", "Anya", "Ben", "Blake", "Carmen", "Clara",
        "Dana", "Dante", "Elena", "Eli", "Faye", "Felix", "Grace", "Grant",
        "Harper", "Hugo", "Iris", "Ivan", "Jade", "James", "Kara", "Kai",
        "Leo", "Luna", "Max", "Mira", "Nadia", "Nate", "Olive", "Oscar",
        "Piper", "Paul", "Quinn", "Ray", "Rosa", "Remy", "Sage", "Sam",
        "Tara", "Theo", "Uma", "Uri", "Vera", "Vince", "Wren", "Wyatt",
        "Xena", "Zara"
    ];

    private static readonly string[] LastNames =
    [
        "Archer", "Banks", "Blake", "Booth", "Brooks", "Burke", "Chase",
        "Cole", "Cross", "Dane", "Drake", "Ellis", "Finch", "Flynn",
        "Frost", "Grant", "Gray", "Hart", "Hayes", "Hunt", "Keane",
        "Knox", "Lane", "Locke", "March", "Mason", "Nash", "North",
        "Owens", "Park", "Pierce", "Quinn", "Reeves", "Ridge", "Rowe",
        "Shaw", "Sloan", "Stone", "Tate", "Thorne", "Voss", "Wade",
        "Walsh", "Ward", "Wells", "West", "Wolfe", "York", "Young",
        "Zane"
    ];
}
