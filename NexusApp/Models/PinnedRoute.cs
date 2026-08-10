namespace NexusApp.Models;

// PinnedRoute was renamed to AcceptedRoute on 2026-08-09 (trade/cargo fusion spec, section 1): a
// pin is a bookmark with no end state, while an accepted route is work taken on, with a lifecycle
// (Accepted -> Loaded -> Sold). The type, with its full history comment, now lives in
// Models/AcceptedRoute.cs. Nothing in the codebase references PinnedRoute any longer.
//
// This file was left in place, empty, rather than deleted or renamed. The house rule on file
// deletion (and on a move that deletes the source, which a rename is) requires an explicit
// per-instance approval that was not obtained while making this change - so the physical file
// stays until Zach reviews and approves its removal.
