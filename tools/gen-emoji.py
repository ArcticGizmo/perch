#!/usr/bin/env python3
"""Regenerate the embedded emoji dataset Perch's picker searches.

Source: github/gemoji `db/emoji.json` (canonical GitHub emoji list — carries the
shortcode aliases, human keywords, category and the Unicode version of every emoji).
We keep the whole set: gemoji tops out at Unicode 15.0, which both a current Windows 11
(Segoe UI Emoji) and a current macOS (Apple Color Emoji) render, so every row is safe
cross-platform. The Unicode version is kept as a trailing column so a future build can
tighten the cutoff without re-fetching.

Usage:
    curl -sSL https://raw.githubusercontent.com/github/gemoji/master/db/emoji.json -o emoji.json
    python tools/gen-emoji.py emoji.json src/Perch.Core/Data/emoji-data.tsv

Output is a tab-separated file, one emoji per line, UTF-8, LF:
    <emoji>\t<name>\t<space-separated keyword tokens>\t<unicode-version>
The keyword column folds together the name words, the shortcode aliases (both the raw
`thumbs_up` form and a spaced `thumbs up` form), the tags and the category, lowercased
and de-duplicated, so a search over that one column catches all the ways people spell it.
"""
import json
import re
import sys

# The maximum Unicode emoji version to include. gemoji currently tops out at 15.0; both
# current Windows 11 and current macOS render through here, so nothing is dropped today.
# Lower this (e.g. to 14.0) to trade a handful of the newest glyphs for older-OS safety.
MAX_UNICODE_VERSION = (15, 0)


def parse_version(v: str):
    if not v:
        return (0, 0)                       # blank == very old, universally supported
    parts = v.split(".")
    major = int(parts[0]) if parts and parts[0].isdigit() else 0
    minor = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else 0
    return (major, minor)


def tokens_for(entry):
    """Build the de-duplicated, lowercased keyword token list for one emoji."""
    seen = set()
    out = []

    def add(text):
        for tok in re.split(r"[\s_\-]+", (text or "").lower()):
            tok = tok.strip()
            if tok and tok not in seen:
                seen.add(tok)
                out.append(tok)

    add(entry.get("description", ""))
    for alias in entry.get("aliases", []):
        add(alias)                          # spaced form: thumbs up
        a = alias.lower()
        if a and a not in seen:             # raw shortcode form: thumbs_up / +1
            seen.add(a)
            out.append(a)
    for tag in entry.get("tags", []):
        add(tag)
    add(entry.get("category", ""))
    return out


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    src, dst = sys.argv[1], sys.argv[2]

    with open(src, "r", encoding="utf-8") as f:
        data = json.load(f)

    lines = []
    skipped = 0
    for entry in data:
        emoji = entry.get("emoji", "")
        name = (entry.get("description") or "").strip()
        if not emoji or not name:
            continue
        ver = parse_version(entry.get("unicode_version", ""))
        if ver > MAX_UNICODE_VERSION:
            skipped += 1
            continue
        kw = " ".join(tokens_for(entry))
        vtext = entry.get("unicode_version", "") or "0"
        # Guard against any stray tab/newline sneaking into a field.
        for field in (emoji, name, kw, vtext):
            assert "\t" not in field and "\n" not in field, field
        lines.append(f"{emoji}\t{name}\t{kw}\t{vtext}")

    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")

    print(f"wrote {len(lines)} emoji to {dst} (skipped {skipped} above Unicode "
          f"{MAX_UNICODE_VERSION[0]}.{MAX_UNICODE_VERSION[1]})")


if __name__ == "__main__":
    main()
