namespace NexusApp.Models;

// PinnedRoute was renamed to AcceptedRoute on 2026-08-09 (trade/cargo fusion spec, section 1): a
// pin is a bookmark with no end state, while an accepted route is work taken on, with a lifecycle
// (Accepted -> Loaded -> Sold). The type, with its full history comment, now lives in
// Models/AcceptedRoute.cs. Nothing in the codebase references PinnedRoute any longer.
//
// The persisted JSON key is still AppSettings.PinnedRoutes, deliberately: it is the property name
// in every existing settings.json, and renaming it would drop every saved route on upgrade.
//
// This placeholder is scheduled for removal.
