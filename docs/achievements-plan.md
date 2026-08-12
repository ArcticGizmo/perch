# Achievements (the "whoops" ledger)

Perch's first achievement system. It rewards a very specific, self-inflicted moment: submitting a
Claude Code prompt, thinking better of it, hitting `ctrl+c`, and re-typing. We call each one a **whoops**.

## What a "whoops" is, exactly

The transcript (`~/.claude/projects/{enc-cwd}/{sessionId}.jsonl`) is an append-only tree: every record
carries a `uuid` and a `parentUuid`. A normal turn adds one `type:"user"` prompt whose `parentUuid` is the
previous leaf. When you cancel a just-submitted prompt and re-type, Claude Code writes **both** prompts, and
crucially **both share the same `parentUuid`** — a fork. The abandoned branch never produces an assistant
reply (you cancelled before it could).

So a whoops is detected as:

> Two or more **genuine user prompts** sharing the same `parentUuid`. Each such fork contributes
> `(childCount - 1)` whoopses (the surviving branch is not a whoops; every superseded sibling is).

"Genuine user prompt" reuses `TranscriptReader.IsGenuineUserPrompt`'s rules: `type:"user"`, message content
is plain text or an array carrying a `text` block (not a `tool_result`), and not a slash-command echo
(`<command-name>` / `<command-message>` / `<local-command-stdout>`) or an interrupt marker
(`[Request interrupted by user`).

### Validation against real data (2026-08-12)

A scan of all 680 local transcripts found **84 whoopses across 72 sessions**. Every fork's abandoned branch
had **zero** assistant descendants — i.e. all 84 were the clean "cancelled before it answered" flavour, none
were deliberate history-rewrites. So we do **not** need an assistant-descendant check to disambiguate; the
bare forked-`parentUuid` signal is sufficient and cheap. (Highest single session: 3.)

This means the achievement counter and the detector agree on the same definition, and a fixture test can pin
the exact tree shape from session `ceafce69` (`"This is text"` → `"This is text: edited"`).

## The achievements

Cumulative (across every session, all-time):

| Threshold | Emoji | Name | Flavour (unlock toast) |
|-----------|:-----:|------|------------------------|
| 5 | ✏️ | Second Thoughts | "Unsent 5 prompts. On reflection…" |
| 25 | 🔄 | Backspace Bandit | "25 prompts sent to the shadow realm." |
| 100 | 📏 | Measure Twice | "100 rewrites. The perch of caution." |
| 250 | 🌀 | The Eternal Redo | "250 whoops. Certainty is for other people." |

Secret (shown as `🔒 ???` until earned):

| Trigger | Emoji | Name | Flavour |
|---------|:-----:|------|---------|
| 5 whoops in one session | 🫠 | Indecisive | "Five whoops, one session. Everything's fine." |

## Architecture (as built)

Perch already had a full achievement framework (catalog → service → store → toast/card/grid UI), all driven
by a lifetime `StatsReport` (+ `RangeReport`). Whoops plugged straight in — no new persistence, notification,
or UI code:

- **Counting** rides the existing full-history stats pass in `SessionStatsService.ParseSession`. During the
  single per-transcript walk it tallies genuine typed prompts by `parentUuid` (`SessionDayData.PromptsByParent`,
  gated by a new `IsGenuineTypedPrompt`), and folds `count-1` per shared parent into a `Whoops` count. This
  surfaces on `StatsReport.Whoops` (all-time cumulative). For the secret, `FoldSession` tracks the max whoops
  any single session contributed to a day, exposed as `RangeReport.MaxSessionWhoops` (the same per-(session,day)
  approximation `LongestSession` already uses).
- **Catalog** — a new `Group("Whoops", …)` in `AchievementCatalog.BuildFamilies`: a `Family.Levelled` ladder
  (`whoops`, rungs 5/25/100/250) reading `Ctx.Whoops`, plus a `Family.Hidden` secret (`indecisive`) reading
  `Ctx.MaxSessionWhoops >= 5`.
- **Everything downstream is unchanged**: `AchievementService.Sync` commits newly-reached rungs to
  `AchievementStore` (once-only), and `App.CheckAchievements` recounts on each session finish and toasts /
  reveals via the existing pipeline. No `AppSettings` or store schema change was needed — the total is
  recomputed from transcripts every check; the store only remembers "already celebrated".

### Badges

| Id | Emoji | Name | Threshold |
|----|:-----:|------|-----------|
| `whoops.secondthoughts`  | 💭 | Second Thoughts  | 5 lifetime |
| `whoops.backspacebandit` | 🔄 | Backspace Bandit | 25 lifetime |
| `whoops.measuretwice`    | 📏 | Measure Twice    | 100 lifetime |
| `whoops.eternalredo`     | 🌀 | The Eternal Redo | 250 lifetime |
| `indecisive.indecisive`  | 🫠 | Indecisive (secret) | 5 in one session |

> **Note:** Second Thoughts uses 💭 (thought balloon), not the originally-floated ✏️ — the pencil was already
> taken by the `the-editor` Tools badge, and two identical glyphs on the wall reads as a bug.

### Tests

`SessionStatsServiceTests.ParseSession_CountsWhoopsFromForkedPrompts` (fork counting + command-echo/interrupt
exclusion), and `AchievementCatalogTests` (`Whoops_LevelsUpThroughTheLadder`,
`Indecisive_IsSecretAndEarnedAtFiveWhoopsInOneSession`, group placement). Eyeballed via
`render <dir>` → `achievements_*.png`.
