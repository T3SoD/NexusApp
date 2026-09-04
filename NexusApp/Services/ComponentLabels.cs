using System;

namespace NexusApp.Services;

// Render-time glue for the component label setting: views hand a display name to the
// method matching their surface class and get back either the same string or the
// decorated form ("Mirage - S1 - Stealth - A"). Only rendered display text goes through
// here; identity strings, log lines, exports, and raw Game.log echoes stay canonical, so
// the label can never leak into joins, files, or diagnostics.
public static class ComponentLabels
{
    private static bool _loggedUnavailable;

    /// <summary>Display form for a Blueprint Library surface (rows, drill labels, the
    /// detail header). Decorates at LIBRARY and EVERYWHERE.</summary>
    public static string Library(string name) =>
        Apply(name, ComponentLabelMode.DecoratesLibrary(Scope));

    /// <summary>Display form for every other component-name surface (collection log,
    /// import previews, network rows). Decorates at EVERYWHERE only.</summary>
    public static string Global(string name) =>
        Apply(name, ComponentLabelMode.DecoratesEverywhere(Scope));

    /// <summary>Display form for the wallet's purchase rows, where names come from the
    /// whole item domain: ships, weapons, and vehicles share display names with components
    /// (Eclipse, Predator, Nova), so decoration is gated on the purchase token's component
    /// family and a missing token stays plain.</summary>
    public static string GlobalForToken(string? itemToken, string name)
    {
        if (!ComponentLabelMode.DecoratesEverywhere(Scope) || string.IsNullOrEmpty(name)) return name;
        try
        {
            var cat = ComponentAttributeCatalog.Instance;
            return cat.IsComponentToken(itemToken) ? cat.Decorate(name) : name;
        }
        catch (Exception ex)
        {
            LogUnavailableOnce(ex);
            return name;
        }
    }

    private static ComponentLabelScope Scope =>
        ComponentLabelMode.Parse(App.Settings?.Current.ComponentLabels);

    private static string Apply(string name, bool decorate)
    {
        if (!decorate || string.IsNullOrEmpty(name)) return name;
        try
        {
            return ComponentAttributeCatalog.Instance.Decorate(name);
        }
        catch (Exception ex)
        {
            LogUnavailableOnce(ex);
            return name;
        }
    }

    // A missing/corrupt embedded table is a build defect; render plain names and say so
    // once rather than break every list that shows one.
    private static void LogUnavailableOnce(Exception ex)
    {
        if (_loggedUnavailable) return;
        _loggedUnavailable = true;
        Logger.Info($"[UI] component attribute catalog unavailable: {ex.Message}");
    }
}
