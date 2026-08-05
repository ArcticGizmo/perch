#!/usr/bin/env python3
"""
Perch calendar probe - read the *next meeting* out of new Outlook's local cache.

New Outlook (olk.exe / Microsoft.OutlookForWindows) is a WebView2 app. Its mailbox
+ calendar are cached locally as a Chromium IndexedDB store:

    %LOCALAPPDATA%\\Microsoft\\Olk\\EBWebView\\Default\\IndexedDB\\
        https_outlook.office.com_0.indexeddb.leveldb   (+ ...\\.blob)

That's LevelDB (Snappy-compressed blocks) holding V8 structured-clone values. This
script reads it with Google's `dfindexeddb` library (pip-installable), profiles the
schema, and best-effort extracts calendar events (title / start / end), then prints
the next meeting.

IT PRINTS TWO THINGS:
  1. A SCHEMA REPORT  -> object-store names + field PATHS and TYPES only, no values.
     Written to schema-report.txt. This is SAFE TO SHARE (no PII) so the extractor
     can be tuned if it misses.
  2. UPCOMING MEETINGS -> real titles + times. Printed to your console ONLY. Not
     written to disk unless you pass --save-meetings.

Nothing is sent anywhere. It only reads local files (read-only, via a snapshot copy).

Install (see run.cmd for the one-click version):
    py -3 -m pip install --no-deps dfindexeddb
    py -3 -m pip install "python-snappy>=0.7" zstd
(`--no-deps` avoids dfindexeddb's pin on python-snappy==0.6.1, which needs a C compiler
on Python 3.11+; the modern python-snappy 0.7.x is cramjam-backed with pure wheels.)

Usage:
    py -3 probe.py                 # profile + extract, print next meeting
    py -3 probe.py --schema-only   # only the non-PII schema report (safe to share)
    py -3 probe.py --store <name>  # limit extraction to one object store
    py -3 probe.py --path <dir>    # point at a different .leveldb folder
    py -3 probe.py --days 14       # lookahead window for the "upcoming" list
    py -3 probe.py --save-meetings # ALSO write meetings to meetings-local.txt (PII!)

Tip: for the cleanest read, close new Outlook first (optional - a live snapshot copy
is attempted either way).
"""
from __future__ import annotations

import argparse
import ctypes
import json
import os
import re
import shutil
import sys
import tempfile
from collections import Counter
from ctypes import wintypes
from datetime import datetime, timedelta, timezone
from pathlib import Path


# ----------------------------------------------------------------------------- paths
def default_leveldb_path() -> Path:
    local = os.environ.get("LOCALAPPDATA", "")
    return Path(local) / "Microsoft" / "Olk" / "EBWebView" / "Default" / "IndexedDB" \
        / "https_outlook.office.com_0.indexeddb.leveldb"


# ---------------------------------------------------------------- locked-file reader
# New Outlook holds these files open. CreateFileW with full share flags lets us read
# them anyway (same trick the PowerShell probe used). No writing, ever.
_GENERIC_READ = 0x80000000
_FILE_SHARE_ALL = 0x00000001 | 0x00000002 | 0x00000004  # READ | WRITE | DELETE
_OPEN_EXISTING = 3
_FILE_ATTRIBUTE_NORMAL = 0x80
_INVALID_HANDLE = ctypes.c_void_p(-1).value

_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
_kernel32.CreateFileW.restype = wintypes.HANDLE
_kernel32.CreateFileW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
    wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE,
]
_kernel32.ReadFile.restype = wintypes.BOOL
_kernel32.ReadFile.argtypes = [
    wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD), ctypes.c_void_p,
]
_kernel32.CloseHandle.argtypes = [wintypes.HANDLE]


def read_shared(path: Path) -> bytes:
    """Read a file even if another process holds it open with a write lock."""
    handle = _kernel32.CreateFileW(
        str(path), _GENERIC_READ, _FILE_SHARE_ALL, None,
        _OPEN_EXISTING, _FILE_ATTRIBUTE_NORMAL, None,
    )
    if handle == _INVALID_HANDLE:
        raise OSError(ctypes.get_last_error(), f"CreateFileW failed for {path}")
    try:
        chunks = []
        buf = ctypes.create_string_buffer(1 << 20)  # 1 MiB
        read = wintypes.DWORD(0)
        while True:
            ok = _kernel32.ReadFile(handle, buf, len(buf), ctypes.byref(read), None)
            if not ok:
                raise OSError(ctypes.get_last_error(), f"ReadFile failed for {path}")
            if read.value == 0:
                break
            chunks.append(buf.raw[: read.value])
        return b"".join(chunks)
    finally:
        _kernel32.CloseHandle(handle)


def snapshot(src_dir: Path, dst_dir: Path) -> None:
    """Copy every file under src_dir to dst_dir (recursively) via the shared reader."""
    dst_dir.mkdir(parents=True, exist_ok=True)
    for f in src_dir.rglob("*"):
        if not f.is_file():
            continue
        rel = f.relative_to(src_dir)
        dst = dst_dir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        try:
            dst.write_bytes(read_shared(f))
        except OSError as e:
            print(f"  ! skipped {rel}: {e}", file=sys.stderr)


# -------------------------------------------------- normalize dfindexeddb value types
def simplify(v, depth=0):
    """Convert dfindexeddb wrapper types (JSArray/JSSet/Null/Undefined/RegExp) and
    nested containers into plain Python. Detected by class name to stay tolerant of
    the library's internal module layout."""
    if depth > 8:
        return v
    tn = type(v).__name__
    if tn in ("Undefined", "Null"):
        return None
    if tn == "RegExp":
        return str(v)
    if tn == "ObjectStoreDataValue":
        # The real object-store row: unwrap to the blink-deserialized object inside.
        return simplify(getattr(v, "value", None), depth + 1)
    if tn in ("JSArray", "JSSet"):
        return [simplify(x, depth + 1) for x in getattr(v, "values", [])]
    if isinstance(v, dict):
        return {str(k): simplify(val, depth + 1) for k, val in v.items()}
    if isinstance(v, (list, tuple)):
        return [simplify(x, depth + 1) for x in v]
    return v


def coerce(value):
    """Expand JSON-string / bytes values into Python objects where possible."""
    if isinstance(value, (bytes, bytearray)):
        try:
            value = value.decode("utf-8")
        except UnicodeDecodeError:
            return value
    if isinstance(value, str):
        s = value.strip()
        if s[:1] in "{[":
            try:
                return json.loads(s)
            except (json.JSONDecodeError, ValueError):
                return value
    return value


# ----------------------------------------------------------------- schema profiling
def walk_key_paths(value, prefix="", depth=0, max_depth=4, out=None):
    """Yield (path, type-name) for every key reachable in a value. No values leak."""
    if out is None:
        out = []
    if depth > max_depth:
        return out
    value = coerce(value)
    if isinstance(value, dict):
        for k, v in value.items():
            path = f"{prefix}.{k}" if prefix else str(k)
            out.append((path, type(coerce(v)).__name__))
            walk_key_paths(v, path, depth + 1, max_depth, out)
    elif isinstance(value, (list, tuple)) and value:
        walk_key_paths(value[0], prefix + "[]", depth + 1, max_depth, out)
    return out


# ------------------------------------------------------------------- event heuristic
def _norm(k) -> str:
    return str(k).lower().replace("_", "").replace("-", "")


SUBJECT_KEYS = {"subject", "title", "normalizedsubject", "displayname"}
START_KEYS = {"start", "startdate", "starttime", "startdatetime", "starttimeutc",
              "starttimestamp", "begin", "startwhole"}
END_KEYS = {"end", "enddate", "endtime", "enddatetime", "endtimeutc",
            "endtimestamp", "endwhole"}
ALLDAY_KEYS = {"isallday", "allday"}


def deep_items(value, depth=0, max_depth=6):
    """Yield (normalized-key, raw-value) for every dict entry anywhere in value."""
    value = coerce(value)
    if depth > max_depth:
        return
    if isinstance(value, dict):
        for k, v in value.items():
            yield _norm(k), v
            yield from deep_items(v, depth + 1, max_depth)
    elif isinstance(value, (list, tuple)):
        for v in value:
            yield from deep_items(v, depth + 1, max_depth)


def to_datetime(v):
    """Best-effort coerce many datetime encodings to an aware UTC datetime."""
    v = coerce(v)
    if isinstance(v, datetime):                # dfindexeddb already decodes V8 Dates
        return v if v.tzinfo else v.replace(tzinfo=timezone.utc)
    if isinstance(v, dict):                    # nested {dateTime: ..., timeZone: ...}
        for k, inner in deep_items(v):
            if k in {"datetime", "date", "value", "ticks", "utc", "iso"}:
                dt = to_datetime(inner)
                if dt:
                    return dt
        return None
    if isinstance(v, str):
        s = v.strip()
        if s.endswith("Z"):
            s = s[:-1] + "+00:00"
        # Exchange emits 7-digit fractional seconds; fromisoformat only accepts <=6.
        s = re.sub(r"(\.\d{6})\d+", r"\1", s)
        try:
            dt = datetime.fromisoformat(s)
            return dt if dt.tzinfo else dt.replace(tzinfo=timezone.utc)
        except ValueError:
            return None
    if isinstance(v, (int, float)):
        n = float(v)
        try:
            if n > 1e17:                       # .NET ticks (100ns since year 1)
                return (datetime(1, 1, 1, tzinfo=timezone.utc)
                        + timedelta(microseconds=n / 10))
            if n > 1e16:                       # Windows FILETIME (100ns since 1601)
                return (datetime(1601, 1, 1, tzinfo=timezone.utc)
                        + timedelta(microseconds=n / 10))
            if n > 1e12:                       # epoch milliseconds
                return datetime.fromtimestamp(n / 1000, tz=timezone.utc)
            if n > 1e9:                        # epoch seconds
                return datetime.fromtimestamp(n, tz=timezone.utc)
        except (OverflowError, ValueError, OSError):
            return None
    return None


def extract_event(value):
    """Return {'title','start','end','all_day'} if value looks like a calendar event."""
    title = start = end = None
    all_day = False
    for k, raw in deep_items(value):
        if title is None and k in SUBJECT_KEYS and isinstance(coerce(raw), str):
            title = coerce(raw)
        elif start is None and k in START_KEYS:
            start = to_datetime(raw)
        elif end is None and k in END_KEYS:
            end = to_datetime(raw)
        elif k in ALLDAY_KEYS and coerce(raw) is True:
            all_day = True
    if start is None:
        return None
    return {"title": title or "(no subject)", "start": start, "end": end,
            "all_day": all_day}


# --------------------------------------------------------------------------- report
def fmt_local(dt: datetime | None) -> str:
    if dt is None:
        return "?"
    return dt.astimezone().strftime("%a %d %b %Y  %H:%M")


def main() -> int:
    ap = argparse.ArgumentParser(description="Probe new Outlook's local cache for the next meeting.")
    ap.add_argument("--path", type=Path, default=default_leveldb_path(),
                    help="path to the *.leveldb folder (default: new Outlook / outlook.office.com)")
    ap.add_argument("--schema-only", action="store_true",
                    help="only emit the non-PII schema report (safe to share)")
    ap.add_argument("--store", default=None, help="limit extraction to this object-store name")
    ap.add_argument("--store-id", type=int, default=None,
                    help="limit extraction to this numeric object_store_id (names are "
                         "often None, so use this once the report shows which id is the calendar)")
    ap.add_argument("--days", type=int, default=7, help="lookahead window for 'upcoming' (default 7)")
    ap.add_argument("--save-meetings", action="store_true",
                    help="ALSO write meetings (PII) to meetings-local.txt")
    ap.add_argument("--no-snapshot", action="store_true",
                    help="read the DB in place instead of snapshotting first")
    ap.add_argument("--max-records", type=int, default=0,
                    help="cap total records scanned (0 = no cap)")
    ap.add_argument("--max-v8-version", type=int, default=50,
                    help="accept V8 serialization versions up to this (default 50; the "
                         "library caps at 15, but new Outlook/Edge emits newer). Set to "
                         "15 to restore the stock ceiling.")
    args = ap.parse_args()

    # dfindexeddb imports snappy at module load; give a clear message if deps missing.
    try:
        import snappy  # noqa: F401  (dfindexeddb.leveldb.ldb imports this)
    except ImportError:
        print('Missing "snappy". Run:\n    py -3 -m pip install "python-snappy>=0.7"',
              file=sys.stderr)
        return 2
    try:
        from dfindexeddb.indexeddb.chromium import record as chromium_record
        from dfindexeddb.indexeddb.chromium import v8 as v8mod
        from dfindexeddb.indexeddb.chromium import definitions as v8defs
    except ImportError:
        print("Missing dfindexeddb. Run (see run.cmd):\n"
              "    py -3 -m pip install --no-deps dfindexeddb\n"
              '    py -3 -m pip install "python-snappy>=0.7" zstd', file=sys.stderr)
        return 2

    # New Outlook runs on a recent Edge/WebView2 whose V8 serializer emits a wire-format
    # version newer than the library's ceiling (LATEST_VERSION=15), so every value fails
    # with "Unsupported header". Patch ReadHeader to (a) record the versions we actually
    # see and (b) accept up to --max-v8-version. V8's format is usually additive, so
    # lifting the ceiling reads older-style content fine; if a genuinely new tag appears
    # it'll surface as a different (counted) parse error instead.
    v8_versions: Counter = Counter()
    _max_v8 = args.max_v8_version

    def _patched_read_header(self):
        if self._ReadTag() != v8defs.V8SerializationTag.VERSION:
            return False
        _, self.version = self.decoder.DecodeUint32Varint()
        v8_versions[self.version] += 1
        return self.version <= _max_v8

    v8mod.ValueDeserializer.ReadHeader = _patched_read_header

    leveldb = args.path
    if not leveldb.exists():
        print(f"LevelDB folder not found:\n    {leveldb}", file=sys.stderr)
        print("Is new Outlook installed / has it synced? Override with --path.", file=sys.stderr)
        return 2

    # --- snapshot (read-only) so we don't touch the live DB -------------------------
    # dfindexeddb's FolderReader wants a folder whose name ends in .leveldb (it derives
    # the sibling .blob folder from that), so keep the original folder name in the copy.
    workdir = None
    if args.no_snapshot:
        ldb_path = leveldb
    else:
        workdir = Path(tempfile.mkdtemp(prefix="perch-cal-"))
        ldb_path = workdir / leveldb.name
        print(f"Snapshotting DB to {ldb_path} ...")
        snapshot(leveldb, ldb_path)
        # Copy the sibling .blob folder too, if present, so FolderReader finds it and
        # doesn't choke on a missing blob dir (some dfindexeddb releases treat that as
        # a hard error). We still pass load_blobs=False below - this is just so the
        # folder exists next to the copied .leveldb.
        blob_src = leveldb.with_name(leveldb.name.replace(".leveldb", ".blob"))
        if blob_src.is_dir():
            print(f"Snapshotting blob folder {blob_src.name} ...")
            snapshot(blob_src, workdir / blob_src.name)

    try:
        reader = chromium_record.FolderReader(ldb_path)
    except Exception as e:  # noqa: BLE001 - forensics lib, keep exploring on any error
        print(f"Failed to open IndexedDB: {type(e).__name__}: {e}", file=sys.stderr)
        _cleanup(workdir)
        return 1

    # per-store accumulators (keyed by "db / store") ---------------------------------
    store_paths: dict[str, Counter] = {}
    store_top: dict[str, Counter] = {}
    store_types: dict[str, Counter] = {}
    store_count: Counter = Counter()
    store_events: Counter = Counter()
    events: list[dict] = []
    total = 0

    # dfindexeddb swallows per-record ParserError/DecoderError and prints them (with full
    # tracebacks) to stderr, so our own try/except never sees them and a naive counter
    # reads 0. Capture stderr during the scan to count them honestly and to keep the
    # traceback flood out of the console.
    import io
    import re as _re

    class _ErrTap(io.TextIOBase):
        def __init__(self):
            self.count = 0
            self.reasons: Counter = Counter()

        def write(self, s):  # noqa: ANN001
            for line in s.splitlines():
                if line.startswith("Error parsing Indexeddb record:"):
                    self.count += 1
                    m = _re.search(r"record: (.*?) at offset", line)
                    self.reasons[m.group(1) if m else "?"] += 1
            return len(s)

    err_tap = _ErrTap()

    # blobs aren't needed for calendar events; skipping avoids copying the blob tree.
    try:
        record_iter = reader.GetRecords(load_blobs=False)
    except TypeError:
        record_iter = reader.GetRecords()

    real_stderr = sys.stderr
    sys.stderr = err_tap
    try:
        while True:
            try:
                rec = next(record_iter)
            except StopIteration:
                break
            except Exception:  # noqa: BLE001 - a bad record shouldn't stop the scan
                err_tap.count += 1
                continue

            store_name = getattr(rec, "object_store_name", None)
            db_id = getattr(rec, "database_id", None)
            store_id = getattr(rec, "object_store_id", None)
            if args.store and store_name != args.store:
                continue
            if args.store_id is not None and store_id != args.store_id:
                continue
            key = f"db{db_id} / store{store_id}" + (f" ({store_name})" if store_name else "")

            raw = getattr(rec, "value", None)
            raw_tn = type(raw).__name__
            total += 1
            store_count[key] += 1
            store_types.setdefault(key, Counter())[raw_tn] += 1
            if total % 20000 == 0:
                print(f"  ...{total} records", file=real_stderr)

            # Only object-store data rows carry real objects; index entries are lists
            # (noise). Profile + event-match just the unwrapped object rows.
            if raw_tn == "ObjectStoreDataValue":
                val = simplify(raw)
                if isinstance(val, dict):
                    store_top.setdefault(key, Counter()).update(str(k) for k in val)
                    paths = store_paths.setdefault(key, Counter())
                    for path, tname in walk_key_paths(val):
                        paths[f"{path}: {tname}"] += 1
                if not args.schema_only:
                    ev = extract_event(val)
                    if ev:
                        events.append(ev)
                        store_events[key] += 1

            if args.max_records and total >= args.max_records:
                break
    finally:
        sys.stderr = real_stderr

    parse_errors = err_tap.count

    # --- build schema report (NON-PII) ---------------------------------------------
    v8_summary = ", ".join(f"v{v}={c}" for v, c in sorted(v8_versions.items())) or "(none)"
    err_summary = "; ".join(f"{r} x{c}" for r, c in err_tap.reasons.most_common(8)) or "(none)"
    lines = [f"LevelDB: {leveldb}",
             f"records parsed OK: {total}",
             f"records that FAILED to parse (swallowed by lib): {parse_errors}",
             f"  failure reasons: {err_summary}",
             f"V8 wire-format versions seen: {v8_summary}  "
             f"(library stock ceiling=15, this run allowed <= {args.max_v8_version})", ""]
    # Surface object-bearing stores first (the calendar is one of them); rank by
    # event-like hits, then by number of profiled object rows.
    def _rank(k):
        return (store_events[k], sum(store_paths.get(k, Counter()).values()))
    for key in sorted(store_count, key=lambda k: (-_rank(k)[0], -_rank(k)[1], -store_count[k])):
        paths = store_paths.get(key, Counter())
        lines.append(f"=== {key}  ({store_count[key]} records) ===")
        lines.append(f"    value-types: {dict(store_types[key])} | "
                     f"event-like: {store_events[key]}")
        if key in store_top:
            lines.append(f"    top-level keys: {sorted(store_top[key])}")
        for entry, cnt in paths.most_common(60):
            lines.append(f"      {cnt:>6}  {entry}")
        lines.append("")

    out_dir = Path(__file__).resolve().parent
    report_path = out_dir / "schema-report.txt"
    report = "\n".join(lines)
    report_path.write_text(report, encoding="utf-8")
    print("\n" + "=" * 70)
    print("SCHEMA REPORT (no values, safe to share) written to:")
    print(f"    {report_path}")
    print("=" * 70)
    print(report)

    if args.schema_only:
        _cleanup(workdir)
        return 0

    # --- meetings (PII: console only unless --save-meetings) ------------------------
    seen = set()
    uniq = []
    for ev in events:
        k = (ev["title"], ev["start"].isoformat() if ev["start"] else None)
        if k in seen:
            continue
        seen.add(k)
        uniq.append(ev)

    now = datetime.now(timezone.utc)
    horizon = now + timedelta(days=args.days)
    upcoming = sorted(
        (e for e in uniq
         if e["start"] and e["start"] <= horizon
         and ((e["end"] or e["start"]) >= now)),
        key=lambda e: e["start"],
    )

    print("\n" + "#" * 70)
    print(f"# UPCOMING MEETINGS  (PII - your eyes only)   next {args.days} days")
    print(f"# parsed {len(uniq)} unique event-like records from the cache")
    print("#" * 70)
    if not upcoming:
        print("\n(no upcoming events matched the heuristic)")
        print("If the cache clearly has meetings, share schema-report.txt with me and")
        print("I'll tune the field matchers to the real shape.")
    else:
        nxt = upcoming[0]
        print(f"\n>>> NEXT: {nxt['title']}")
        print(f"    starts {fmt_local(nxt['start'])}"
              + (f"  ends {fmt_local(nxt['end'])}" if nxt['end'] else ""))
        if len(upcoming) > 1:
            print("\n  then:")
            for e in upcoming[1:12]:
                print(f"    {fmt_local(e['start'])}   {e['title']}")

    if args.save_meetings and upcoming:
        out = out_dir / "meetings-local.txt"
        out.write_text(
            "\n".join(f"{fmt_local(e['start'])} - {fmt_local(e['end'])}  {e['title']}"
                      for e in upcoming),
            encoding="utf-8",
        )
        print(f"\n(meetings written to {out} - contains PII, delete when done)")

    _cleanup(workdir)
    return 0


def _cleanup(workdir):
    if workdir:
        shutil.rmtree(workdir, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
