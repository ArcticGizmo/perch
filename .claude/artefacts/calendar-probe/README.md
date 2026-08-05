# Calendar probe (new Outlook local cache)

A throwaway exploration script to see what calendar data we can pull out of **new
Outlook's** local WebView2 cache — no auth, no Entra app, no network. It uses Google's
pip-installable **`dfindexeddb`** to read the Chromium IndexedDB store new Outlook keeps
at:

```
%LOCALAPPDATA%\Microsoft\Olk\EBWebView\Default\IndexedDB\https_outlook.office.com_0.indexeddb.leveldb
```

and best-effort extracts calendar events (title / start / end) to find your **next
meeting**.

This is the "brittle path" from `docs/next-meeting-integration-plan.md` — the goal
here is to learn the on-disk shape, not to ship it.

## Run it

```cmd
cd .claude\artefacts\calendar-probe
run.cmd
```

Or install by hand:

```cmd
py -3 -m pip install --no-deps dfindexeddb
py -3 -m pip install "python-snappy>=0.7" zstd
py -3 probe.py
```

**Why the `--no-deps` dance?** `dfindexeddb` pins `python-snappy==0.6.1`, which has no
Windows wheel for Python 3.11+ and would try to compile against the C `libsnappy`. So we
install `dfindexeddb` without its deps and add the modern **`python-snappy` 0.7.x**
(cramjam-backed, pure wheels) plus `zstd` ourselves — same API, no compiler.

Requires **Python 3.10+**. For the cleanest read you *can* close new Outlook first,
but it's not required — the script snapshots the DB with a shared-read copy so it
works while Outlook is running. It only ever **reads**; it never writes to the cache.

## What it prints — and the PII split

The script deliberately separates two outputs so you can involve me safely:

1. **`schema-report.txt`** — object-store names, and the field **paths + types** found
   in the records (e.g. `start.dateTime: str`). **No values.** This has **no PII**, so
   you can paste it back to me and I'll tune the extractor to the real field names.

2. **Upcoming meetings** — actual titles and times. **Printed to your console only.**
   Nothing is written to disk unless you add `--save-meetings` (which drops a
   `meetings-local.txt` you should delete afterwards). **I never see this.**

## Options

| Flag | Effect |
|---|---|
| `--schema-only` | Emit only the non-PII schema report (safe to share). Skips event extraction. |
| `--store NAME` | Only scan one object store (use once we know where events live). |
| `--path DIR` | Point at a different `*.leveldb` folder (e.g. Teams, or a copied DB). |
| `--days N` | Lookahead window for the "upcoming" list (default 7). |
| `--save-meetings` | Also write meetings to `meetings-local.txt` (PII — delete after). |
| `--no-snapshot` | Read the DB in place instead of snapshotting first. |
| `--max-records N` | Cap total records scanned (0 = no cap). Useful for a fast first look. |

## Expected first-run outcome

One of three things, all useful:

- **It finds your meetings** → great, we know the shape works; next step is porting the
  parse into `Perch.Core`.
- **It finds event-like records but times/titles look wrong** → the field matchers need
  tuning. Send me `schema-report.txt`.
- **It finds nothing event-like** (values are opaque/protobuf, or calendar lives
  elsewhere) → also send me `schema-report.txt`; it tells us whether the data is
  parseable at all or whether we need a different store/origin (or the whole approach
  is a dead end).

## Troubleshooting: "Unsupported header"

New Outlook runs on a recent Edge/WebView2, whose V8 serializer emits a wire-format
version newer than `dfindexeddb`'s ceiling (`LATEST_VERSION = 15`). Stock, every value
fails to deserialize with `ParserError: Unsupported header` (the library prints these to
stderr and *swallows* them — which is why an older build of this probe wrongly reported
"0 parse errors").

The probe now:
- **lifts the ceiling** to `--max-v8-version` (default 50), since V8's format is normally
  additive so older-style content still reads; and
- **reports the versions it actually saw** and an honest parsed-OK vs failed count at the
  top of `schema-report.txt`.

Read the top of `schema-report.txt` after a run:
- `V8 wire-format versions seen: v16=...` with `records parsed OK` climbing → the bump
  worked; calendar data should be extractable.
- versions seen but records still fail with a *different* reason → Edge's format added
  tags the library can't read yet; this path may be blocked until `dfindexeddb` updates.
- garbage version numbers (e.g. `v918273`) → the Blink envelope offset is off, a deeper
  format change.

Send me the top few lines of `schema-report.txt` (no PII) and I'll tell you which case
you're in.

## Suggested first command

```cmd
run.cmd --schema-only
```

Start here — it's fast-ish and produces the safe-to-share report without touching any
meeting contents. Then run plain `run.cmd` to see if the next-meeting extraction works
for you locally.
