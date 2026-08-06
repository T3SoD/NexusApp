using System.IO;

namespace NexusApp.Services;

// Resolves the per-profile data root once per process. A normal launch uses %AppData%\NexusApp.
// A launch carrying --demo-profile uses %AppData%\NexusApp_demo instead: a fully separate,
// disposable profile seeded from the embedded StarlightHauler demo dataset, so public
// screenshots can be taken without the live profile ever being read or written.
public static class AppPaths
{
    public const string DemoArg = "--demo-profile";

    // Pure core, testable headless: args plus the AppData folder in, profile root out.
    public static string ResolveRoot(string[] args, string appDataDir) =>
        Path.Combine(appDataDir, args.Contains(DemoArg) ? "NexusApp_demo" : "NexusApp");

    public static bool IsDemoProfile { get; } =
        Environment.GetCommandLineArgs().Contains(DemoArg);

    public static string Root { get; } = ResolveRoot(
        Environment.GetCommandLineArgs(),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    // The demo root regardless of which profile THIS process runs in (the live instance
    // needs it to seed and reset the demo profile from the Admin tab).
    public static string DemoRoot => ResolveRoot(
        new[] { DemoArg },
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    public static string LogsDir => Path.Combine(Root, "logs");
}
