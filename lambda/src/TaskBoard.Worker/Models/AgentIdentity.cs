using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TaskBoard.Worker.Models;

/// <summary>
/// Human-friendly identity for an agent process instance.
/// Generated once at startup; threaded through logging and comments.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FamilyName"/> is randomly assigned per process session — all
/// agents in a single polling run share the same family name (e.g. "Smith").
/// </para>
/// <para>
/// The given name (first name) is derived deterministically from a
/// <c>provider:model</c> key via SHA-256, so the same provider+model
/// combination always produces the same given name across any session.
/// Use <see cref="ResolveGivenName"/> to look it up, or
/// <see cref="FormatAgentName"/> to build the full display string.
/// </para>
/// </remarks>
public sealed record AgentIdentity(string FamilyName, string MachineName)
{
    /// <summary>Session-level display: "FamilyName on MachineName"</summary>
    public string DisplayName => $"{FamilyName} on {MachineName}";

    /// <summary>
    /// Full agent name for a specific provider+model combination.
    /// Returns "GivenName FamilyName on MachineName".
    /// Falls back to <see cref="DisplayName"/> when provider is null or empty.
    /// </summary>
    public string FormatAgentName(string? provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return DisplayName;
        var givenName = ResolveGivenName(provider, model);
        return $"{givenName} {FamilyName} on {MachineName}";
    }

    /// <summary>
    /// Deterministically maps a provider+model combination to a given name.
    /// The same input always produces the same name, regardless of session or machine.
    /// Hash is SHA-256 of "{provider}:{model}" lowercased (stable across .NET versions).
    /// </summary>
    public static string ResolveGivenName(string? provider, string? model)
    {
        var key = $"{provider ?? ""}:{model ?? ""}".ToLowerInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var index = (int)(BinaryPrimitives.ReadUInt32BigEndian(hashBytes) % (uint)GivenNames.Length);
        return GivenNames[index];
    }

    public static AgentIdentity Generate()
    {
        var family = FamilyNames[Random.Shared.Next(FamilyNames.Length)];
        var machine = Environment.MachineName;
        return new AgentIdentity(family, machine);
    }

    public static IReadOnlyList<string> AvailableGivenNames => GivenNames;
    public static IReadOnlyList<string> AvailableFamilyNames => FamilyNames;

    private static readonly string[] GivenNames =
    [
        // Original 50
        "Ada", "Alan", "Alice", "Anya", "Ben", "Blake", "Carmen", "Clara",
        "Dana", "Dante", "Elena", "Eli", "Faye", "Felix", "Grace", "Grant",
        "Harper", "Hugo", "Iris", "Ivan", "Jade", "James", "Kara", "Kai",
        "Leo", "Luna", "Max", "Mira", "Nadia", "Nate", "Olive", "Oscar",
        "Piper", "Paul", "Quinn", "Ray", "Rosa", "Remy", "Sage", "Sam",
        "Tara", "Theo", "Uma", "Uri", "Vera", "Vince", "Wren", "Wyatt",
        "Xena", "Zara",
        // 150 additional
        "Aaron", "Abby", "Ace", "Adele", "Aiden", "Alexis", "Alina", "Amber",
        "Andre", "Anna", "Anton", "Ariel", "Aria", "Ash", "Asher", "Avery",
        "Axel", "Bea", "Beck", "Bram", "Bren", "Brett", "Brook", "Brynn",
        "Cade", "Caleb", "Cara", "Cass", "Celeste", "Chase", "Chloe", "Cody",
        "Cole", "Colin", "Colt", "Coral", "Cyan", "Cyrus", "Dash", "Dawn",
        "Dean", "Devon", "Dex", "Drake", "Drew", "Dylan", "Elan", "Eliot",
        "Elise", "Ella", "Ember", "Emil", "Erin", "Esme", "Ethan", "Evan",
        "Eve", "Finn", "Fiona", "Flint", "Flynn", "Gael", "Gage", "Gale",
        "Gem", "Gia", "Hazel", "Heath", "Hera", "Hiro", "Hollis", "Holt",
        "Hux", "Idris", "Indigo", "Ivy", "Jaden", "Jax", "Jay", "Jett",
        "Jo", "Joel", "Jules", "June", "Kade", "Kane", "Kira", "Kit",
        "Knox", "Kyra", "Lana", "Lane", "Lars", "Layla", "Lena", "Lennox",
        "Lexa", "Lila", "Lily", "Lin", "Logan", "Lola", "Luca", "Lucy",
        "Lyra", "Mace", "Mack", "Maeve", "Mars", "Mav", "Maya", "Mel",
        "Miles", "Milo", "Misha", "Moss", "Nova", "Nyx", "Odin", "Ollie",
        "Orin", "Orla", "Owen", "Paige", "Pax", "Phoenix", "Pip", "Poe",
        "Porter", "Priya", "Rayne", "Reed", "Ren", "Rex", "Rio", "River",
        "Roan", "Robin", "Rowan", "Ruby", "Rune", "Ryan", "Sable", "Salem",
        "Sasha", "Scout", "Seth", "Shay", "Sierra", "Silas",
    ];

    private static readonly string[] FamilyNames =
    [
        // Original 50
        "Archer", "Banks", "Blake", "Booth", "Brooks", "Burke", "Chase",
        "Cole", "Cross", "Dane", "Drake", "Ellis", "Finch", "Flynn",
        "Frost", "Grant", "Gray", "Hart", "Hayes", "Hunt", "Keane",
        "Knox", "Lane", "Locke", "March", "Mason", "Nash", "North",
        "Owens", "Park", "Pierce", "Quinn", "Reeves", "Ridge", "Rowe",
        "Shaw", "Sloan", "Stone", "Tate", "Thorne", "Voss", "Wade",
        "Walsh", "Ward", "Wells", "West", "Wolfe", "York", "Young",
        "Zane",
        // 150 additional
        "Acton", "Adair", "Adler", "Aiken", "Ainsworth", "Aldridge", "Allard", "Alton",
        "Ames", "Anders", "Ashby", "Ashford", "Atwood", "Ayers", "Bain", "Baird",
        "Barker", "Barnes", "Barrett", "Barrow", "Beaumont", "Bedford", "Bell", "Benson",
        "Birch", "Bishop", "Blaine", "Blair", "Blythe", "Bowen", "Brant", "Bray",
        "Briggs", "Bright", "Brock", "Brody", "Browning", "Bryce", "Buchanan", "Buckley",
        "Burns", "Cairns", "Caldwell", "Calloway", "Camden", "Cannon", "Carlyle", "Carr",
        "Cassidy", "Cavendish", "Chambers", "Chandler", "Clay", "Clayton", "Clifton", "Coburn",
        "Cochran", "Colby", "Colton", "Conrad", "Coombs", "Cooper", "Corbett", "Crane",
        "Croft", "Cromwell", "Culver", "Curran", "Dale", "Dalton", "Davenport", "Davies",
        "Davis", "Deacon", "Devlin", "Donnelly", "Douglas", "Drummond", "Dunbar", "Duncan",
        "Dunn", "Dunning", "Easton", "Edgar", "Edison", "Edmunds", "Edwards", "Elder",
        "Elton", "Emery", "Emmett", "Engel", "Evans", "Ewing", "Faulk", "Faulkner",
        "Fenton", "Ferguson", "Field", "Fielding", "Finley", "Fisher", "Fitch", "Fletcher",
        "Ford", "Foster", "Fowler", "Frank", "Fraser", "Freeman", "Fuller", "Furlong",
        "Gallagher", "Garner", "Garrett", "Garrison", "Gibson", "Gifford", "Gilbert", "Giles",
        "Gill", "Glenn", "Greer", "Grimes", "Hadley", "Hale", "Hall", "Hammond",
        "Hanlon", "Hardin", "Harlow", "Harmon", "Harrington", "Harris", "Hatch", "Hawkins",
        "Hayward", "Hogan", "Holland", "Holman", "Hooper", "Horn", "Houston", "Howard",
        "Howell", "Hudson", "Hurst", "Hyde", "Ingram", "Irving",
    ];
}
