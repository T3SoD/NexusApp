# User Guide Redesign — Design

**Date:** 2026-06-11
**Status:** Approved, implemented (shipping in the next release)
**Component:** `NexusApp/Views/HelpDialog.cs`

## Problem

The in-app user guide (Help window) was a single 600×660 non-resizable modal holding
~85 undifferentiated bullets across 9 sections in one long scroll. Problems:

- A wall of text with no findability — no contents, jump-to, or search.
- Flat hierarchy: every bullet equal weight; UI control glyphs (▶ ■ ⊕ ⧉) buried mid-sentence.
- Ignored the app's own UX language — the Mining Codex and Blueprint Library are two-panel browsers.

## Goals

- Make any topic reachable in one action.
- Give each topic real hierarchy: a lead summary, called-out key controls, then details.
- Feel native by adopting the app's two-pane browser pattern.

## Design

A **two-pane topic browser**, resizable (default 880×600, min 720×480), theme-aware.

**Left pane (248px):**
- A search box that filters the topic list by title, lead, item text, or key label
  (case-insensitive). Auto-selects the first match. Esc clears the filter, then closes
  the dialog if already empty.
- The topic list (Overlay · Auto-scan · RS Decoder · Refinery Tracker · Blueprint
  Library · Mining Codex · Shopping List · Appearance). The selected row gets an accent
  left-bar, a tint, and accent text — mirroring the nav rail.

**Right pane (scrolling):** the selected topic, rendered as:
- Title + icon.
- A **lead** sentence (the "what this is"), larger and dimmer.
- A **KEY CONTROLS** row of bordered chips (glyph in accent + label in dim) — pulling the
  buried inline glyphs into a scannable legend.
- The detail **bullets** (all existing accurate content), plus the inline RS example image.

**Footer (unchanged):** Close (left) + Replay Tutorial (right, sets `TutorialRequested`).

## Content model

```
record HelpKey(string Glyph, string Label);
record Topic(string Icon, string Title, string Lead, HelpKey[] Keys, string[] Items, string? Image = null);
```

Same facts as before, reshaped. The `img:<path>` convention for inline images is preserved.
Bullets that reference the real `🛒` toolbar/ingredient buttons are kept verbatim (they match
the actual UI); `🛒` is intentionally kept out of the new key-chips to respect the no-emoji rule.

## Structure

All in `HelpDialog.cs`, one cohesive component: `BuildLeftPane`, `BuildTopicRows`,
`ApplyFilter(query)`, `SelectTopic(topic)`, `BuildContent(topic)`, `BuildKeyChip`, `BuildFooter`.

## Testing

WPF can't build in WSL; the CI "Build check" is the compile gate. Manual verification on
Windows via `test_nexus.ps1` → Help (?): two-pane renders, topic clicks switch content,
search filters and auto-selects, both themes (gold/teal), window resizes, Replay Tutorial
still launches the tour.
