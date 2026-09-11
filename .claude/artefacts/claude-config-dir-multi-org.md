# CLAUDE_CONFIG_DIR and running two orgs from one login

How `CLAUDE_CONFIG_DIR` works, and a practical recipe for running **two orgs at once** from a
single SSO account — plus how a per-org launcher scheme (the `claude-envs` convention) automates
the same thing.

---

## 1. The one variable everything hangs on

Claude Code reads a single environment variable, **`CLAUDE_CONFIG_DIR`**. *Everything* it stores
for a session lives under that one directory:

| File under the config dir | What it holds |
| --- | --- |
| `.claude.json` | The **OAuth org binding** — `oauthAccount.organizationName` / `Uuid`, plus a per-cwd `projects` map |
| `.credentials.json` | The OAuth **token itself** (on Windows this is the real credential store, not a pointer) |
| `settings.json` | `enabledPlugins`, `extraKnownMarketplaces`, `statusLine`, hooks, permissions, theme |
| `projects/<slug>/*.jsonl` | Transcripts (append-only), with `memory/` (auto-memory) inside each slug dir |
| `sessions/` | Per-session permission mode + transient PID-keyed state |
| `plugins/` | Marketplace clones, plugin payloads, catalog cache, inventory |
| `history.jsonl` | Up-arrow prompt recall (**not** the `--resume` index) |
| `file-history/` | Edit snapshots behind `/undo` and rewind |

If `CLAUDE_CONFIG_DIR` is unset it defaults to `%USERPROFILE%\.claude` (`~/.claude`). Point it at
an **empty** directory and you get a pristine, unauthenticated Claude Code that writes its own
`.claude.json` there and reports `Not logged in`.

### Why this matters for multiple orgs

Claude Code binds **one config directory to one org**, and there is **no org-switch command** —
`claude auth` offers only `login`, `logout`, `status`. With a single SSO login that has access to
several orgs, moving between them means a full **logout → login** round trip that re-binds your
*only* config directory **globally**. Every `claude` you start afterwards is on the new org,
whether you wanted it or not. You cannot have two orgs live at once.

The entire scheme below — and the DIY recipe — is just this:

> **One directory per org, each signed in to its own org, plus a launcher that sets
> `CLAUDE_CONFIG_DIR` for the invoked process only.**

Everything else exists to stop that split from costing you the single shared transcript history
and single plugin download you had with one directory.

---

## 2. How a per-org launcher scheme uses it

The `claude-envs` convention is a Windows PowerShell scheme that automates the split. It creates a
scheme root at `%USERPROFILE%\.claude-envs` and, from a `manifest.json`, generates one config
directory and one launcher per org:

```
claude-orga   ->  CLAUDE_CONFIG_DIR=…\.claude-envs\envs\orga   ->  Org A
claude-orgb   ->  CLAUDE_CONFIG_DIR=…\.claude-envs\envs\orgb   ->  Org B
claude-orgc   ->  CLAUDE_CONFIG_DIR=…\.claude-envs\envs\orgc   ->  Org C
                              │
                              └── projects\  sessions\  plugins\  ──junction──> …\shared\
```

### What it shares vs. isolates — and why

The clever half is that splitting into N directories would normally fragment your conversation
history and force N plugin downloads. So the scheme **shares the expensive stuff** and **isolates
only what identifies the org**.

**Shared** (one copy for all orgs, via NTFS **directory junctions**):

- `shared\projects\` — transcripts, so `--resume` spans orgs. Auto-memory rides along because
  `memory\` lives *inside* each project slug dir.
- `shared\sessions\` — permission mode per session.
- `shared\plugins\` — marketplace clones, payloads, catalog, inventory. One download / update
  cycle for every org (~30 MB total, not 30 MB × orgs). Seeded once by copying `~/.claude\plugins`,
  so a fresh install costs **zero re-download**.

**Isolated** (per environment, never linked):

- `.claude.json` — the org binding. *Share this and every environment collapses onto one org.*
- `.credentials.json` — the token.
- `settings.json` — so `enabledPlugins` can differ per product (the plugin *store* is shared;
  *enablement* is not).

A `doctor` check asserts those three are real files and **fails if any becomes a link**.

### Key implementation choices (each was learned by running it)

- **Junctions, not symlinks.** A directory junction (`mklink /J` / `New-Item -ItemType Junction`)
  needs neither Developer Mode nor elevation, unlike a directory symlink. It is also the *correct*
  primitive: Claude Code writes its JSON inventories with a write-new-then-rename, and the rename
  lands *inside* the junction target, so it stays shared. Hardlinking individual files would
  silently un-share on that atomic replace.
- **`.cmd` shims, not a `$PROFILE` function.** In PowerShell `$env:X` is process-global, so the
  obvious `function claude-x { $env:CLAUDE_CONFIG_DIR='...'; claude @args }` **leaks** — after it
  returns the variable is still set and every later bare `claude` in that terminal is mis-pointed.
  A `.cmd` shim runs in a **child process**, so it cannot leak by construction, and one artifact
  serves PowerShell, cmd.exe and Git Bash.
- **`claude` resolved via `where /q claude`** at launch, so auto-updates or a native→npm switch
  keep working.
- **`history.jsonl` is a file, so a junction can't share it.** Default `history: fresh` gives each
  env its own recall; opt-in `history: shared` **symlinks** them all at one file (symlink creation
  needs elevation once; *following* one does not).
- **`file-history` is deliberately per-environment.** Claude Code's periodic cleanup deletes a
  `file-history` dir holding no session subdirectories — and on Windows deleting a dir removes the
  junction pointing at it, so a shared one silently un-shares itself every cleanup cycle. Cost:
  `/undo` loses another env's file snapshots, but resuming still replays full conversation history.
- **UTF-8 without BOM** for all JSON — PowerShell 5.1's `-Encoding UTF8` emits a BOM and Node's
  `JSON.parse` throws on it, silently breaking `settings.json`.

### The org-mismatch guard (the genuinely valuable safety net)

With one SSO login it's easy to pick the wrong org at `/login`, and nothing else tells you. So each
manifest entry can declare the org it's *meant* to be on; an `adopt-org` step records the live
`oauthAccount.organizationName` after you sign in, and `doctor` fails from then on if the live
binding disagrees. It only ever **adds** a missing `org` — a declared one that disagrees is
reported, never overwritten, because that mismatch is the whole reason the check exists.

### Behaviours that look like faults but aren't

Worth knowing because they'll waste your time otherwise:

- `claude -p` (print mode) sessions never appear in the `--resume` picker (no `history.jsonl`
  entry, no title) — still resumable by id / `--continue`.
- The `--resume` picker is **scoped to the current directory**.
- `claude plugin details` only reports a plugin **enabled in the env you ask from**; a disabled one
  says `not found`, which reads like a broken install.
- A repo whose own `.claude/statusline.sh` composes yours expects `statusline-command.sh` in the
  config dir; a fresh env doesn't have it, so the statusline renders only the repo's own segment.

### Scope / limits

- **Windows only, on purpose.** On macOS the OAuth token lives in the **login Keychain**, which is
  per-user not per-config-dir, so per-environment sign-in may not isolate at all. (See §4.)
- **Cross-org visibility is real:** one shared transcript store means every env can read every
  other org's conversations and they all show in `--resume`. Fine for internal orgs; if any org
  sits behind a client/contractual boundary, use per-`product` shared stores instead.
- Cross-machine and WSL sync are out of scope (the transcript slug is derived from the absolute
  project path, so the same repo lands under different slugs).

---

## 3. Doing it yourself, for two orgs

You don't need the whole machine. The mechanism is two config dirs and two launchers. Pick the
level of effort you want.

### Option A — the 30-second manual version (no sharing)

This is all you strictly need for two live orgs. Two directories, two shims. You lose the shared
transcript/plugin cleverness, but for two orgs that's often fine.

**1. Make two config dirs** (any location):

```powershell
mkdir "$env:USERPROFILE\.claude-orgs\orgA"
mkdir "$env:USERPROFILE\.claude-orgs\orgB"
```

**2. Create a `.cmd` shim per org** on your PATH (e.g. in a folder you add to `PATH`). Use a child
process so the variable can't leak into your terminal:

`claude-a.cmd`
```cmd
@echo off
setlocal
set "CLAUDE_CONFIG_DIR=%USERPROFILE%\.claude-orgs\orgA"
claude %*
```

`claude-b.cmd` — identical but `orgB`.

> **Do not** use a PowerShell function that sets `$env:CLAUDE_CONFIG_DIR` — it's process-global and
> stays set after the function returns, mis-pointing every later bare `claude` in that terminal.
> A `.cmd`/child process cannot leak.

For **Git Bash**, add extensionless twins with the path in **Windows form** (the native `claude.exe`
can't read `/c/Users/...`):

`claude-a`
```bash
exec env CLAUDE_CONFIG_DIR='C:\Users\<you>\.claude-orgs\orgA' claude "$@"
```

**3. Sign each in, once**, in its own terminal:

```
claude-a        # then /login, PICK ORG A
claude-b        # then /login, PICK ORG B
```

Now two terminals can sit on two orgs indefinitely. Verify the variable doesn't leak:

```powershell
$env:CLAUDE_CONFIG_DIR      # empty
claude-a --version
$env:CLAUDE_CONFIG_DIR      # still empty
```

That's the whole trick. Everything below is optional polish.

### Option B — share transcripts and plugins between the two

If you want `--resume` to span both orgs and only one plugin download, junction the shared stores
into both config dirs. **Only ever junction `projects`, `sessions`, `plugins` — never
`.claude.json`, `.credentials.json`, or `settings.json`.**

```powershell
$root = "$env:USERPROFILE\.claude-orgs"
mkdir "$root\shared\projects","$root\shared\sessions","$root\shared\plugins"

# seed the plugin store once from your existing config (zero re-download)
robocopy "$env:USERPROFILE\.claude\plugins" "$root\shared\plugins" /E | Out-Null

foreach ($org in 'orgA','orgB') {
  foreach ($name in 'projects','sessions','plugins') {
    $link = "$root\$org\$name"
    if (Test-Path $link) { cmd /c rmdir "$link" }        # remove real dir first if present
    cmd /c mklink /J "$link" "$root\shared\$name" | Out-Null
  }
}
```

Notes / gotchas that bite in practice:

- Junction **before** first `claude-a` run, or move the real dir's contents into `shared\` first —
  a non-empty real dir where a junction belongs can't just be linked over.
- **Never `Remove-Item -Recurse`** a junction — depending on the tool it can delete *through* the
  link into the shared store. Use `cmd /c rmdir "<link>"` (no `/s`) to remove just the link.
- Check whether something is a junction with `fsutil reparsepoint query "<path>"` — `dir /al` lists
  ordinary files too and is not a filter.
- Leave `file-history` **per-env** (don't share it) — Claude Code's cleanup will keep deleting the
  junction.
- Sharing `projects\` also shares **auto-memory** (it lives inside each project slug) — usually
  what you want, but be aware if your two orgs are client-separated.

### Option C — use an existing launcher scheme

If a maintained per-org launcher scheme (the `claude-envs` convention) is available to you, the
lowest-effort path is to run it and trim the manifest to your two orgs. You get `doctor` (the
org-mismatch guard is the real prize), idempotent `sync`, a clean `uninstall` that rescues
transcripts first, and the statusline handling — none of which you'd want to reimplement. Such a
scheme never touches your existing `~/.claude`, your tokens, or your manifest once it exists;
re-running it is the update path.

**Recommendation:** if you want something you fully control and understand in five minutes, **Option
A** (add Option B later if you miss cross-org `--resume`). If a launcher scheme is already set up
for you, **Option C**.

---

## 4. If you're on macOS

Per-org isolation on macOS is unproven and the reason matters for a DIY attempt: the OAuth token is
stored in the **login Keychain**, not in `<config dir>/.credentials.json`, and a Keychain item is
**per-user, not per-config-dir**. So setting `CLAUDE_CONFIG_DIR` per org may isolate the
transcripts/settings but **not the sign-in** — both "orgs" could end up sharing one Keychain
credential. Treat per-org isolation on macOS as *unproven* until you've verified two orgs actually
stay signed in independently. On Windows, `.credentials.json` is the real store and lives in the
config dir, so the isolation is clean.

---

## 5. One-paragraph summary

`CLAUDE_CONFIG_DIR` relocates *everything* Claude Code stores — including the org binding and
credentials — under one directory. One directory therefore equals one org, and there's no switch
command, so the way to run two orgs concurrently is one config dir each plus a **child-process**
launcher (a `.cmd` shim, never a shell function) that sets the variable for that process only. A
per-org launcher scheme productionises that for several orgs, adding shared transcript/plugin stores
via NTFS **junctions** (isolating only `.claude.json` / `.credentials.json` / `settings.json`), a
`doctor` that guards against signing an env into the wrong org, and a pile of hard-won handling for
Windows path quirks and Claude Code's own quirks. For two orgs, Options A–C above get you there in
increasing order of polish.
