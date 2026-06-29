namespace NexusApp.Models;

/// <summary>
/// A person in the user's Blueprint Network - someone whose shared library has been imported.
/// The local user ("self") is represented from <see cref="AppSettings"/>, not as one of these.
/// </summary>
public class NetworkMember
{
    public string Id { get; set; } = "";                                    // stable GUID from the exporter
    public string DisplayName { get; set; } = "";
    public string IdentityKind { get; set; } = NetworkIdentityKind.Handle;  // "handle" | "nickname"
    public string? RsiHandle { get; set; }                                  // set only when shared as a handle
    public DateTime LastUpdatedUtc { get; set; }
    public bool IsSelf { get; set; }
}

/// <summary>
/// A named, user-defined grouping of members (e.g. "Friends", an org). A member can belong to
/// several groups; "Friends" and an org are just groups the user creates.
/// </summary>
public class NetworkGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

/// <summary>How a member chose to be labelled when they exported.</summary>
public static class NetworkIdentityKind
{
    public const string Handle = "handle";
    public const string Nickname = "nickname";
}
