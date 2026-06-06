#!/usr/bin/env python3
"""Post the latest Nexus changelog entry to a Discord channel via webhook.

Runs in GitHub Actions when a release is published. It parses the top entry of
the `Changelog` array in AboutDialog.xaml.cs and posts it. If PREVIOUS_FILE is
provided it only posts when the version differs from that copy; on release
events PREVIOUS_FILE is empty, so it always posts the current top entry (the
release event is itself the signal that this is a new version).

No third-party dependencies — uses only the Python standard library so it
runs on a bare ubuntu-latest runner with no pip install.

Environment variables:
  CURRENT_FILE     path to the current AboutDialog.xaml.cs
  PREVIOUS_FILE    path to the previous commit's copy (may be empty/missing)
  DISCORD_WEBHOOK  Discord channel webhook URL
"""

import json
import os
import re
import sys
import urllib.error
import urllib.request

REPO_URL = "https://github.com/T3SoD/NexusApp"
ACCENT_BLUE = 0x58A6FF  # Nexus' Stanton-blue accent


def extract_top_entry(text):
    """Return (label, [changes]) for the first entry of the Changelog array."""
    idx = text.find("Changelog")
    if idx == -1:
        return None
    eq = text.find("=", idx)
    arr = text.find("[", eq)
    start = text.find("(", arr)
    if start == -1:
        return None

    # Walk to the matching ')' of the first tuple, skipping string contents.
    depth, i, n = 0, start, len(text)
    in_str = esc = False
    end = None
    while i < n:
        c = text[i]
        if in_str:
            if esc:
                esc = False
            elif c == "\\":
                esc = True
            elif c == '"':
                in_str = False
        elif c == '"':
            in_str = True
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                end = i
                break
        i += 1
    if end is None:
        return None

    entry = text[start : end + 1]
    raw = re.findall(r'"((?:[^"\\]|\\.)*)"', entry)
    if not raw:
        return None

    def unescape(s):
        return s.replace('\\"', '"').replace("\\\\", "\\")

    label = unescape(raw[0])
    changes = [unescape(s) for s in raw[1:]]
    return label, changes


def get_version(label):
    m = re.search(r"\d+\.\d+\.\d+", label)
    return m.group(0) if m else label.strip()


def get_date(label):
    return label.split("—")[-1].strip() if "—" in label else ""


def read(path):
    if path and os.path.exists(path) and os.path.getsize(path) > 0:
        with open(path, encoding="utf-8") as f:
            return f.read()
    return ""


def main():
    cur_text = read(os.environ.get("CURRENT_FILE", ""))
    if not cur_text:
        print("CURRENT_FILE is empty or missing", file=sys.stderr)
        sys.exit(1)
    cur = extract_top_entry(cur_text)
    if not cur:
        print("Could not parse current changelog", file=sys.stderr)
        sys.exit(1)
    cur_label, cur_changes = cur
    cur_ver = get_version(cur_label)

    prev_text = read(os.environ.get("PREVIOUS_FILE", ""))
    prev_ver = None
    if prev_text:
        prev = extract_top_entry(prev_text)
        if prev:
            prev_ver = get_version(prev[0])

    if cur_ver == prev_ver:
        print(f"No version change (still {cur_ver}); nothing to post.")
        return

    webhook = os.environ.get("DISCORD_WEBHOOK", "").strip()
    if not webhook:
        print("DISCORD_WEBHOOK not set", file=sys.stderr)
        sys.exit(1)
    if not webhook.startswith("https://"):
        print("DISCORD_WEBHOOK does not look like a URL", file=sys.stderr)
        sys.exit(1)

    # Blank line between bullets so each one is visually separated in Discord.
    bullets = "\n\n".join("• " + c for c in cur_changes) or "• See the in-app changelog for details."
    if len(bullets) > 4000:
        bullets = bullets[:3990].rstrip() + "\n…"

    embed = {
        "title": f"\U0001faa8 Nexus {cur_ver} released",
        "url": f"{REPO_URL}/releases",
        "description": bullets,
        "color": ACCENT_BLUE,
    }
    date = get_date(cur_label)
    if date:
        embed["footer"] = {"text": date}

    payload = {"username": "Nexus Updates", "embeds": [embed]}
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        webhook,
        data=data,
        headers={
            "Content-Type": "application/json",
            # Discord sits behind Cloudflare, which 403s (error 1010) the default
            # "Python-urllib" User-Agent. A real UA string is required.
            "User-Agent": "NexusApp-ChangelogBot/1.0 (+https://github.com/T3SoD/NexusApp)",
        },
    )
    try:
        with urllib.request.urlopen(req) as resp:
            print(f"Posted Nexus {cur_ver} to Discord (HTTP {resp.status}).")
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        print(f"Discord rejected the post (HTTP {e.code}): {body}", file=sys.stderr)
        sys.exit(1)
    except urllib.error.URLError as e:
        print(f"Could not reach Discord: {e.reason}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
