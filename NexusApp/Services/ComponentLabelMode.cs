namespace NexusApp.Services;

/// <summary>Where component names carry their attribute label. Off is the default: the
/// display every existing install already has.</summary>
public enum ComponentLabelScope { Off, Library, Everywhere }

// The enum-and-string seam for the component label setting (the CloseAction idiom):
// AppSettings stores a lowercase string so hand-edited values degrade to Off, the pills
// and log lines share one Label() spelling, and render sites ask the two Decorates
// questions instead of re-deriving scope rules locally.
public static class ComponentLabelMode
{
    public const string Off = "off";
    public const string Library = "library";
    public const string Everywhere = "everywhere";

    public static ComponentLabelScope Parse(string? stored) => stored switch
    {
        Library => ComponentLabelScope.Library,
        Everywhere => ComponentLabelScope.Everywhere,
        _ => ComponentLabelScope.Off,          // unknown/hand-edited value degrades to default, never throws
    };

    public static string ToStored(ComponentLabelScope s) => s switch
    {
        ComponentLabelScope.Library => Library,
        ComponentLabelScope.Everywhere => Everywhere,
        _ => Off,
    };

    /// <summary>The pill label, so the Settings row and any log line name it the same way.</summary>
    public static string Label(ComponentLabelScope s) => s switch
    {
        ComponentLabelScope.Library => "LIBRARY",
        ComponentLabelScope.Everywhere => "EVERYWHERE",
        _ => "OFF",
    };

    /// <summary>Do Blueprint Library surfaces decorate component names at this scope?</summary>
    public static bool DecoratesLibrary(ComponentLabelScope s) => s != ComponentLabelScope.Off;

    /// <summary>Do all other component-name surfaces decorate at this scope?</summary>
    public static bool DecoratesEverywhere(ComponentLabelScope s) => s == ComponentLabelScope.Everywhere;
}
