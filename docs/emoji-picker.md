# Emoji picker

Perch has its own emoji chooser (`src/Perch.App/Windows/EmojiPickerWindow.cs`) instead of leaning on the OS
emoji dialog (Win + .). It's used by the social feed's reaction picker (`OverlayCanvas.Feed.ShowReactionPicker`)
and is reusable for any "pick an emoji" need.

## Behaviour

- **Default (empty box):** shows your **recently used** emoji, most-recent first. On first run (no history yet)
  it shows a small **popular** fallback set so the grid is never blank.
- **Search:** typing filters the whole cross-platform emoji set by name, GitHub shortcode alias or keyword —
  `fire` → 🔥, `tada` → 🎉, `thumbs up` / `thumbsup` / `+1` → 👍. A multi-word query is AND-ed across keywords,
  so it narrows. **Enter** picks the top match.
- **Type/paste any emoji:** typing or pasting an actual emoji offers it directly as a highlighted "use this" chip
  (and Enter picks it), so nothing is limited to the dataset.
- Picking an emoji records it as recently used, and the picker closes (also on Esc or when it loses focus).

## Data set (`EmojiCatalog`)

The searchable set lives in `Perch.Core` so it's testable and head-agnostic:

- `src/Perch.Core/Data/EmojiCatalog.cs` — loads the embedded dataset once and exposes `All` and
  `Search(query, limit)`. Ranking: exact name (1000) > name prefix (400); then every query word must match some
  keyword token, exact (100) > prefix (60) > substring (25), else the entry is dropped. Ties fall back to the
  dataset's natural (roughly Unicode) order, so results are stable.
- `src/Perch.Core/Data/emoji-data.tsv` — the embedded resource (`LogicalName` `Perch.emoji-data.tsv`, wired in
  `Perch.Core.csproj`). Tab-separated: `emoji · name · keyword tokens · unicode-version`.

### Cross-platform compatibility

The dataset is generated from GitHub's [gemoji](https://github.com/github/gemoji) `db/emoji.json`, which tops out
at **Unicode 15.0** — a set both a current Windows 11 (Segoe UI Emoji) and a current macOS (Apple Color Emoji)
render — so every search result is safe to show on either head. The Unicode version is kept as the trailing TSV
column so a future build can tighten the cutoff (e.g. to 14.0 for older-OS safety) without re-fetching.

### Regenerating the data

```sh
curl -sSL https://raw.githubusercontent.com/github/gemoji/master/db/emoji.json -o emoji.json
python tools/gen-emoji.py emoji.json src/Perch.Core/Data/emoji-data.tsv
```

`tools/gen-emoji.py` folds each emoji's name words, shortcode aliases (both `thumbs_up` and spaced `thumbs up`),
tags and category into the lowercased, de-duplicated keyword column, and applies the version cutoff
(`MAX_UNICODE_VERSION`).

## Recently-used state

`AppSettings.RecentEmojis` (most-recent-first, de-duplicated, capped at `AppSettings.RecentEmojiCap` = 24) is
maintained by `AppSettings.RecordRecentEmoji`. It's runtime history, not a user-facing setting (excluded in
`SettingsRegistryTests.NotSettings`). The overlay wires it in `App.axaml.cs`: `OverlayCanvas.RecentEmojisProvider`
reads the list and `OverlayCanvas.EmojiUsed` records a pick and `Save()`s. An unwired canvas (the Settings
preview) is null-safe — it shows the popular fallback and remembers nothing.

## Tests

`tests/Perch.Tests/EmojiCatalogTests.cs` covers dataset loading, search ranking (name/alias/shortcut/multi-word),
blank/nonsense queries, the result limit, and the `RecordRecentEmoji` promote/dedupe/cap behaviour.
