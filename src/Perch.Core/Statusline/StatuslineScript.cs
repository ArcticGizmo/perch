namespace Perch.Statusline;

using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Perch.Data;

/// <summary>
/// Generates a <b>standalone</b> statusline command from a Perch template — a self-contained Node
/// script (<c>.mjs</c>) with the engine and the template baked in. <c>settings.json</c> points straight
/// at it (<c>node "…"</c>), so Perch is never invoked at refresh time and the status line keeps working
/// even if Perch is uninstalled or not running. Node is chosen because Claude Code already requires it,
/// so the script has a guaranteed runtime and is identical across Windows/macOS/Linux.
///
/// <para>The embedded engine is a faithful port of <see cref="StatuslineTemplate"/> (same tokens,
/// conditionals, filters, truecolor palette and rounding), including reading <c>git.branch</c> straight
/// off <c>.git/HEAD</c> — the one field Perch used to inject — so nothing is lost by dropping Perch from
/// the loop. A parity test renders the same cases through both and diffs the bytes.</para>
/// </summary>
internal static class StatuslineScript
{
    /// <summary>The generated script's path inside a given config dir — <c>{root}/perch-statusline.mjs</c>.
    /// Each config dir gets its own copy so its <c>settings.json</c> points at a script beside it and the
    /// dirs stay independent (see the config-dir-targeted <see cref="StatuslineInstaller.Apply"/>).</summary>
    public static string ScriptPathFor(string configRoot) => Path.Combine(configRoot, "perch-statusline.mjs");

    /// <summary>Where <c>use</c>/<c>install</c> write the active Perch profile's script by default: beside
    /// Claude Code's own config (the docs' example location), independent of Perch's install dir.</summary>
    public static string DefaultScriptPath => ScriptPathFor(ClaudePaths.ClaudeDir);

    // The header line a generated script carries: "… generated from profile <json-name>." — lets a reader
    // name which profile a config dir's active script came from without re-parsing the whole thing.
    private static readonly Regex HeaderName =
        new(@"generated from profile (""(?:[^""\\]|\\.)*"")", RegexOptions.Compiled);

    /// <summary>The Perch profile name baked into a generated script's header, or null when the file is
    /// missing/unreadable or isn't a Perch-generated script. Best-effort — used to show which config dir is
    /// running which profile.</summary>
    public static string? ProfileNameFromScript(string scriptPath)
    {
        try
        {
            if (!File.Exists(scriptPath)) return null;
            var head = File.ReadAllText(scriptPath);
            var m = HeaderName.Match(head);
            return m.Success ? JsonSerializer.Deserialize<string>(m.Groups[1].Value) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The <c>settings.json → statusLine.command</c> that runs a generated script.</summary>
    public static string CommandFor(string scriptPath) => $"node \"{scriptPath}\"";

    /// <summary>Builds the standalone script for a Perch profile (its <see cref="StatuslineProfile.Template"/>
    /// baked in). Throws if the profile has no template. <paramref name="devMarker"/> forces the dev-instance
    /// marker on/off; <c>null</c> (the default) uses <see cref="AppProfile.IsDev"/> — tests pass <c>false</c>
    /// so the generated script matches the pure engine byte-for-byte.</summary>
    public static string Generate(StatuslineProfile profile, bool? devMarker = null)
    {
        var dev = devMarker ?? AppProfile.IsDev;
        var template = profile.Template
            ?? throw new System.InvalidOperationException("profile has no template");

        // JSON-encode both — a JSON string literal is also a valid JS string literal, so this is safe
        // against quotes, backslashes and newlines in the template.
        var templateLiteral = JsonSerializer.Serialize(template);
        var nameLiteral = JsonSerializer.Serialize(profile.Name);

        // Only spawn `git` for staged/unstaged counts when the template actually asks for them — branch is
        // free (a .git/HEAD read), but the counts need a subprocess, so unused templates stay fast.
        var needsCounts = template.Contains("git.staged") || template.Contains("git.unstaged")
                       || template.Contains("git.changes") || template.Contains("git.dirty");

        // Likewise, only read the config dir's .claude.json for account/org fields when the template
        // asks for them — a cheap file read, but pointless (and a privacy surface) for lines that don't.
        var needsAccount = template.Contains("account.");

        // Read Perch's OWN settings.json (context thresholds + account guardrails) only when the template
        // references perch.* or uses the |ctxcolor filter — otherwise the script never touches it. The
        // absolute path is baked in so the script reads the exact file the generating Perch uses (dev or
        // release, whatever OS), yet defaults sensibly and never errors when that file is absent.
        var needsPerch = template.Contains("perch.") || template.Contains("ctxcolor");
        var perchSettingsLiteral = JsonSerializer.Serialize(AppSettings.SettingsFilePath);

        return Body
            .Replace("@@TEMPLATE@@", templateLiteral)
            .Replace("@@NAME@@", nameLiteral)
            .Replace("@@GITCOUNTS@@", needsCounts ? "true" : "false")
            .Replace("@@ACCOUNT@@", needsAccount ? "true" : "false")
            .Replace("@@PERCH@@", needsPerch ? "true" : "false")
            .Replace("@@PERCHSETTINGS@@", perchSettingsLiteral)
            .Replace("@@DEV@@", dev ? "true" : "false")
            .Replace("@@DEVNOTE@@", dev ? " [generated by a Perch DEV instance]" : "");
    }

    // The script body. @@TEMPLATE@@ / @@NAME@@ are replaced with JSON string literals. Kept as a raw
    // string literal so backslashes (\x1b, regex escapes) are literal. Everything is defensive: any
    // failure prints nothing rather than a stack trace into the status bar.
    private const string Body = """
        #!/usr/bin/env node
        // Perch statusline — standalone, generated from profile @@NAME@@.@@DEVNOTE@@
        // Self-contained: edit freely; no Perch process is involved at runtime.
        import { readFileSync, existsSync, statSync } from 'node:fs';
        import { join, dirname, isAbsolute, resolve } from 'node:path';
        import { homedir } from 'node:os';
        import { execFileSync } from 'node:child_process';

        const TEMPLATE = @@TEMPLATE@@;
        const NEED_GIT_COUNTS = @@GITCOUNTS@@;
        const NEED_ACCOUNT = @@ACCOUNT@@;
        const NEED_PERCH = @@PERCH@@;
        const PERCH_SETTINGS = @@PERCHSETTINGS@@;   // absolute path to Perch's settings.json; may not exist
        const DEV = @@DEV@@;   // baked true when a Perch DEV instance generated this script
        // A loud amber-on-dark " dev " tag prepended to the line so you can tell at a glance that the ACTIVE
        // status line was written by a dev build (dev and release share ~/.claude/perch-statusline.mjs).
        const DEV_MARKER = '\x1b[48;2;227;168;78m\x1b[38;2;20;20;20m dev \x1b[0m ';

        const COL = {
          teal:[70,198,184], amber:[227,168,78], green:[95,191,127], red:[229,104,106],
          yellow:[227,179,65], blue:[91,155,214], violet:[176,133,224], muted:[139,149,166],
        };
        // A colour is a named role (COL) or a custom hex (#rrggbb / rrggbb / #rgb) — terminals do truecolor.
        function hex(s) {
          if (!s) return null;
          let h = s[0] === '#' ? s.slice(1) : s;
          if (h.length === 3) h = h[0]+h[0]+h[1]+h[1]+h[2]+h[2];
          if (!/^[0-9a-fA-F]{6}$/.test(h)) return null;
          return [parseInt(h.slice(0,2),16), parseInt(h.slice(2,4),16), parseInt(h.slice(4,6),16)];
        }
        const ansi = (t, c, bg) => {
          const fg = c ? (COL[c] || hex(c)) : null;
          const bgc = bg ? (COL[bg] || hex(bg)) : null;
          if (!fg && !bgc) return t;
          const parts = [];
          if (fg) parts.push(`38;2;${fg[0]};${fg[1]};${fg[2]}`);
          if (bgc) parts.push(`48;2;${bgc[0]};${bgc[1]};${bgc[2]}`);
          return `\x1b[${parts.join(';')}m${t}\x1b[0m`;
        };
        const sepText = (body) => body === 'sep' ? ' | '
          : body.startsWith('sep:') ? ' ' + (body.slice(4).trim() || '|') + ' ' : null;
        // {{else}} / {{elseif EXPR}} → {is, expr} (expr '' for a plain else). Mirrors IsElse.
        function elseClause(body) {
          if (body === 'else') return { is: true, expr: '' };
          if (body.startsWith('elseif ')) return { is: true, expr: body.slice(7).trim() };
          return { is: false, expr: '' };
        }

        function lookup(path, ctx) {
          let v = ctx;
          for (const part of path.split('.')) {
            if (v != null && typeof v === 'object' && Object.prototype.hasOwnProperty.call(v, part)) v = v[part];
            else return undefined;
          }
          return v;
        }
        const truthy = v =>
          !(v === undefined || v === null || v === false || v === 0 || v === '' || (Array.isArray(v) && v.length === 0));
        function num(v) {
          if (v === undefined || v === null) return null;
          if (typeof v === 'number') return v;
          if (typeof v === 'boolean') return v ? 1 : 0;
          if (typeof v === 'string') { const n = parseFloat(v); return Number.isNaN(n) ? null : n; }
          return null;
        }
        const str = v => (v === undefined || v === null) ? '' : (typeof v === 'boolean' ? (v ? 'true' : 'false') : String(v));

        const pint = (a, fb) => { const v = parseInt(a, 10); return (!Number.isNaN(v) && v > 0) ? v : fb; };
        const fmtK = n => Math.abs(n) >= 1000
          ? (n / 1000).toFixed(Math.abs(n) >= 10000 ? 0 : 1) + 'k'
          : String(Math.trunc(n));
        // Round to `places` decimals (half up), trailing zeros trimmed — built from integer/string ops so it
        // matches StatuslineTemplate.RoundFixed byte-for-byte regardless of Number.toFixed's rounding.
        function roundFixed(value, places) {
          if (!(places >= 0)) places = 0;
          if (places > 15) places = 15;
          const neg = value < 0;
          const scale = Math.pow(10, places);
          let digits = String(Math.floor(Math.abs(value) * scale + 0.5));
          let text;
          if (places === 0) text = digits;
          else {
            if (digits.length <= places) digits = '0'.repeat(places - digits.length + 1) + digits;
            const dot = digits.length - places;
            const frac = digits.slice(dot).replace(/0+$/, '');
            text = frac.length === 0 ? digits.slice(0, dot) : digits.slice(0, dot) + '.' + frac;
          }
          return (neg && text !== '0') ? '-' + text : text;
        }
        function bar(p, cells) {
          let f = Math.round(Math.min(Math.max(p, 0), 100) / 100 * cells);
          f = Math.min(Math.max(f, 0), cells);
          return '█'.repeat(f) + '░'.repeat(cells - f);
        }
        const trunc = (s, max) => s.length <= max ? s : (max <= 1 ? s.slice(0, max) : s.slice(0, max - 1) + '…');
        const human = n => n < 1000 ? String(Math.floor(n)) : n < 1e6 ? String(Math.floor(n / 1000)) + 'k' : String(Math.floor(n / 1e6)) + 'M';
        function dur(ms) { if (ms < 1000) return Math.floor(ms) + 'ms'; const s = Math.floor(ms / 1000); if (s < 60) return s + 's'; if (s < 3600) return Math.floor(s / 60) + 'm'; return Math.floor(s / 3600) + 'h ' + Math.floor((s % 3600) / 60) + 'm'; }
        function until(epoch) { if (epoch === null || epoch === undefined) return ''; let m = Math.floor((epoch - Date.now() / 1000) / 60); if (m < 0) m = 0; return m >= 60 ? Math.floor(m / 60) + 'h ' + (m % 60) + 'm' : m + 'm'; }
        function paceColor(actual, expected) { if (expected < 15) return 'green'; const d = actual - expected; if (d < -10) return 'green'; if (d <= 1) return 'yellow'; return 'red'; }
        function pace(actual, arg, ctx) {
          if (!arg) return null;
          const cut = arg.lastIndexOf(':'); if (cut < 0) return null;
          const win = parseInt(arg.slice(cut + 1), 10); if (!(win > 0)) return null;
          const r = num(lookup(arg.slice(0, cut), ctx)); if (r === null) return null;
          const remaining = r - Date.now() / 1000;
          const expected = Math.min(Math.max((win - remaining) / win * 100, 0), 100);
          return paceColor(actual, expected);
        }
        const intOr = (v, fb) => { const n = parseInt(v, 10); return Number.isFinite(n) ? n : fb; };
        // Colour a 0–100 fill by Perch's context-pressure thresholds (from perch.context, injected below;
        // defaults 50/65/80 when Perch's config isn't available). Below yellow is calm green, warming up to
        // red. Mirrors StatuslineTemplate.ContextColor.
        function ctxColor(value, ctx) {
          const c = (ctx && ctx.perch && ctx.perch.context) || {};
          const y = intOr(c.yellow, 50), o = intOr(c.orange, 65), r = intOr(c.red, 80);
          if (value >= r) return 'red';
          if (value >= o) return 'amber';
          if (value >= y) return 'yellow';
          return 'green';
        }

        function applyVar(spec, ctx) {
          const parts = spec.split('|');
          const val = lookup(parts[0].trim(), ctx);
          let text = str(val); const n = num(val); let color = null;
          for (let i = 1; i < parts.length; i++) {
            const f = parts[i].trim(); if (!f) continue;
            const ci = f.indexOf(':'); const name = (ci < 0 ? f : f.slice(0, ci)).trim();
            const arg = ci < 0 ? null : f.slice(ci + 1).trim();
            switch (name) {
              case 'money': text = (n || 0).toFixed(2); break;
              case 'round': text = roundFixed(n || 0, pint(arg, 0)); break;
              case 'pct':   text = String(Math.round(n || 0)) + '%'; break;
              case 'k':     text = fmtK(n || 0); break;
              case 'upper': text = text.toUpperCase(); break;
              case 'lower': text = text.toLowerCase(); break;
              case 'bar':   text = bar(n || 0, pint(arg, 10)); break;
              case 'trunc': text = trunc(text, pint(arg, 20)); break;
              case 'human': text = human(n || 0); break;
              case 'dur':   text = dur(n || 0); break;
              case 'until': text = until(n); break;
              case 'default': if (text.length === 0) text = arg || ''; break;
              case 'color': color = arg; break;
              case 'pace':  color = pace(n || 0, arg, ctx); break;
              case 'ctxcolor': color = ctxColor(n || 0, ctx); break;
            }
          }
          return { text, color };
        }

        function tokenize(t) {
          const re = /\{\{([#/^!]?)([^}]*)\}\}/g; const out = []; let last = 0, m;
          while ((m = re.exec(t))) {
            if (m.index > last) out.push({ t: 'text', v: t.slice(last, m.index) });
            const sig = m[1], body = m[2].trim();
            if (sig === '!') { /* comment */ }
            else if (sig === '#') out.push({ t: 'open', v: body });
            else if (sig === '^') out.push({ t: 'inv', v: body });
            else if (sig === '/') out.push({ t: 'close', v: body });
            else {
              const sp = sepText(body);
              if (sp !== null) out.push({ t: 'sep', v: sp });
              else { const e = elseClause(body); if (e.is) out.push({ t: 'else', v: e.expr }); else out.push({ t: 'var', v: body }); }
            }
            last = re.lastIndex;
          }
          if (last < t.length) out.push({ t: 'text', v: t.slice(last) });
          return out;
        }
        function build(toks) {
          let i = 0;
          const parseBg = (body) => body.startsWith('bg:') ? body.slice(3).trim() : null;
          const classifyOpen = (tk) => {
            if (tk.t === 'inv') return { mode: 'inv', expr: tk.v };
            if (tk.v.startsWith('if ')) return { mode: 'if', expr: tk.v.slice(3).trim() };
            if (tk.v.startsWith('unless ')) return { mode: 'unless', expr: tk.v.slice(7).trim() };
            return { mode: 'truthy', expr: tk.v };
          };
          // A run of nodes up to (not consuming) the next close/else at this level.
          const parseBlock = () => {
            const nodes = [];
            while (i < toks.length) {
              const tk = toks[i];
              if (tk.t === 'close' || tk.t === 'else') return nodes;
              if (tk.t === 'open' || tk.t === 'inv') nodes.push(parseSection());
              else { nodes.push(tk); i++; }
            }
            return nodes;
          };
          const parseSection = () => {
            const open = toks[i]; i++;   // consume the opening tag
            if (open.t === 'open') {
              const bgArg = parseBg(open.v);
              if (bgArg !== null) {
                const node = { kind: 'bg', color: bgArg, kids: parseBlock() };
                if (i < toks.length && toks[i].t === 'close') i++;
                return node;
              }
            }
            const co = classifyOpen(open);
            const branches = [{ mode: co.mode, expr: co.expr, kids: parseBlock() }];
            let elseKids = null;
            while (i < toks.length && toks[i].t === 'else') {
              const e = toks[i]; i++;
              if (e.v && e.v.length > 0) branches.push({ mode: 'if', expr: e.v, kids: parseBlock() });
              else { elseKids = parseBlock(); break; }
            }
            if (i < toks.length && toks[i].t === 'close') i++;
            return { kind: 'section', branches, elseKids };
          };
          return parseBlock();
        }
        const evalTruthy = (p, ctx) => truthy(lookup(p.trim(), ctx));
        function evalExpr(expr, ctx) {
          expr = expr.trim();
          const m = expr.match(/^(.+?)\s*(==|!=|>=|<=|>|<)\s*(.+)$/);
          if (!m) return evalTruthy(expr, ctx);
          const l = lookup(m[1].trim(), ctx), op = m[2]; let r = m[3].trim();
          const ln = num(l), rn = Number(r), rNum = r !== '' && Number.isFinite(rn);
          if (ln !== null && rNum) {
            switch (op) {
              case '==': return ln === rn; case '!=': return ln !== rn;
              case '>': return ln > rn; case '<': return ln < rn;
              case '>=': return ln >= rn; case '<=': return ln <= rn;
            }
          }
          const unq = s => (s.length >= 2 && ((s[0] === "'" && s.slice(-1) === "'") || (s[0] === '"' && s.slice(-1) === '"'))) ? s.slice(1, -1) : s;
          const ls = str(l), rs = unq(r);
          return op === '==' ? ls === rs : op === '!=' ? ls !== rs : false;
        }
        // Render to an array of pieces {text,color,bg,isSep} so the {{sep}} collapse can drop empty cells.
        function renderPieces(nodes, ctx) {
          const pieces = [];
          const branchActive = (b) => {
            if (b.mode === 'if') return evalExpr(b.expr, ctx);
            if (b.mode === 'unless') return !evalExpr(b.expr, ctx);
            if (b.mode === 'truthy') return evalTruthy(b.expr, ctx);
            if (b.mode === 'inv') return !evalTruthy(b.expr, ctx);
            return false;
          };
          // bg = the background colour arg in force at this depth (from an enclosing {{#bg:…}}); an inner
          // region overrides it for its own children.
          const walk = (ns, bg) => {
            for (const n of ns) {
              if (n.t === 'text') { if (n.v.length) pieces.push({ text: n.v, color: null, bg, isSep: false }); }
              else if (n.t === 'var') { const { text, color } = applyVar(n.v, ctx); if (text.length) pieces.push({ text, color, bg, isSep: false }); }
              else if (n.t === 'sep') pieces.push({ text: n.v, color: null, bg, isSep: true });
              else if (n.kind === 'bg') walk(n.kids, n.color);
              else if (n.kind === 'section') {
                let chosen = null;
                for (const b of n.branches) if (branchActive(b)) { chosen = b.kids; break; }
                if (!chosen && n.elseKids) chosen = n.elseKids;
                if (chosen) walk(chosen, bg);
              }
            }
          };
          walk(nodes, null);
          return pieces;
        }
        // Drop {{sep}} dividers whose adjacent cell is empty/whitespace and collapse doubled ones — so a
        // separator vanishes when a side renders nothing. Mirrors StatuslineTemplate.CollapseSeps.
        function collapse(pieces) {
          if (!pieces.some(p => p.isSep)) return pieces;
          const cells = [[]], seps = [];
          for (const p of pieces) { if (p.isSep) { seps.push(p); cells.push([]); } else cells[cells.length - 1].push(p); }
          const nonEmpty = c => c.some(x => x.text.trim().length > 0);
          const out = []; let any = false;
          for (let i = 0; i < cells.length; i++) {
            if (!nonEmpty(cells[i])) continue;
            if (any) out.push(seps[i - 1]);
            for (const x of cells[i]) out.push(x);
            any = true;
          }
          return out;
        }
        // Drop output lines that render to nothing visible (empty/whitespace once ANSI escapes are ignored) —
        // a conditional that resolves to nothing shouldn't leave a blank row. Mirrors StatuslineTemplate.DropBlankLines.
        const dropBlankLines = (s) => s.indexOf('\n') < 0 ? s
          : s.split('\n').filter(l => l.replace(/\x1b\[[0-9;]*m/g, '').trim().length > 0).join('\n');
        // A backslash before a newline is a soft break (editor readability wrap) — drop it so it doesn't
        // split the line. Mirrors StatuslineTemplate.StripSoftBreaks.
        const render = (tpl, ctx) => {
          const pieces = collapse(renderPieces(build(tokenize(tpl.replace(/\\\r?\n/g, ''))), ctx));
          let out = '';
          for (const p of pieces) out += ansi(p.text, p.color, p.bg);
          return dropBlankLines(out);
        };

        // git.branch straight off .git/HEAD — no subprocess, no Perch.
        function findGitDir(dir) {
          let d = dir;
          while (d) {
            const g = join(d, '.git');
            if (existsSync(g)) {
              if (statSync(g).isDirectory()) return g;
              const m = readFileSync(g, 'utf8').trim().match(/^gitdir:\s*(.+)$/);
              if (m) { const p = m[1].trim(); return isAbsolute(p) ? p : resolve(d, p); }
              return null;
            }
            const parent = dirname(d);
            if (parent === d) break;
            d = parent;
          }
          return null;
        }
        function gitBranch(dir) {
          try {
            const gd = findGitDir(dir); if (!gd) return null;
            const head = join(gd, 'HEAD'); if (!existsSync(head)) return null;
            const t = readFileSync(head, 'utf8').trim(); const pfx = 'ref: refs/heads/';
            return t.startsWith(pfx) ? (t.slice(pfx.length).trim() || null) : null;
          } catch { return null; }
        }
        // staged/unstaged file counts — this one does shell out to git (like a classic bash statusline),
        // but only when the template needs it (NEED_GIT_COUNTS). Any failure yields nothing.
        function gitCounts(dir) {
          try {
            const opt = { cwd: dir, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], timeout: 2000 };
            const count = args => execFileSync('git', args, opt).split('\n').filter(l => l.trim().length > 0).length;
            const staged = count(['diff', '--cached', '--numstat']);
            const unstaged = count(['diff', '--numstat']);
            const changes = staged + unstaged;
            return { staged, unstaged, changes, dirty: changes > 0 };
          } catch { return null; }
        }
        function injectGit(p) {
          const cwd = p.cwd || (p.workspace && p.workspace.current_dir) || '';
          if (!cwd) return;
          const b = gitBranch(cwd);
          if (b) p.git = Object.assign({}, p.git, { branch: b });
          if (NEED_GIT_COUNTS) {
            const c = gitCounts(cwd);
            if (c) p.git = Object.assign({}, p.git, c);
          }
        }

        // account/org for the session's config dir, straight off its .claude.json oauthAccount — a file
        // read, no subprocess, no Perch. Mirrors Perch.Data.ClaudeJsonReader: honour CLAUDE_CONFIG_DIR
        // (else ~/.claude), preferring the file INSIDE the dir but falling back to the parent's
        // ~/.claude.json — the default dir keeps its real login one level up. Signed out / unreadable
        // yields an all-empty, signed_in:false object so {{^account.signed_in}} and |default still work.
        function readSignIn(claudeJsonPath) {
          try {
            if (!existsSync(claudeJsonPath)) return null;
            const a = JSON.parse(readFileSync(claudeJsonPath, 'utf8') || '{}').oauthAccount;
            if (!a) return null;
            const uuid = a.organizationUuid || '';
            return {
              email: a.emailAddress || a.email || '',
              org: uuid ? (a.organizationName || '') : '',
              org_uuid: uuid,
              signed_in: true,
              personal: !uuid,
            };
          } catch { return null; }
        }
        function readAccount() {
          const env = (process.env.CLAUDE_CONFIG_DIR || '').trim();
          const dir = env || join(homedir(), '.claude');
          const inside = readSignIn(join(dir, '.claude.json'));
          if (inside) return inside;
          const parent = dirname(dir);
          if (parent && parent !== dir) {
            const up = readSignIn(join(parent, '.claude.json'));
            if (up) return up;
          }
          return { email: '', org: '', org_uuid: '', signed_in: false, personal: false };
        }
        // Never clobber an account the payload already carries (future-proofs a native field, and keeps
        // rendering deterministic when a caller supplies one).
        function injectAccount(p) {
          if (!NEED_ACCOUNT || (p.account !== undefined && p.account !== null)) return;
          p.account = readAccount();
        }

        // ── Perch's own config: context thresholds + account guardrails ──────────────────────────────
        // Read straight off Perch's settings.json (PERCH_SETTINGS, baked in). Perch-AGNOSTIC by design: if
        // the file is missing/unreadable/uninstalled, every value falls back to a sensible default rather
        // than erroring — thresholds to Perch's shipped 50/65/80, and the guardrail to "no mismatch".
        function readPerchSettings() {
          try {
            if (!PERCH_SETTINGS || !existsSync(PERCH_SETTINGS)) return {};
            return JSON.parse(readFileSync(PERCH_SETTINGS, 'utf8') || '{}') || {};
          } catch { return {}; }
        }
        // Path helpers mirroring Perch.Data.AccountGuard: compare on whole segments so C:\work\acme never
        // matches C:\work\acme-two; case-insensitive everywhere except Linux.
        const CASE_INSENSITIVE = process.platform !== 'linux';
        const pathEq = (a, b) => CASE_INSENSITIVE ? a.toLowerCase() === b.toLowerCase() : a === b;
        function normPath(p) {
          if (!p || !String(p).trim()) return null;
          try { let r = resolve(String(p).trim()); while (r.length > 1 && (r.endsWith('\\') || r.endsWith('/'))) r = r.slice(0, -1); return r; }
          catch { return null; }
        }
        function atOrUnder(target, base) {
          if (!base.length) return false;
          if (pathEq(target, base)) return true;
          if (target.length <= base.length) return false;
          if (!pathEq(target.slice(0, base.length), base)) return false;
          const b = target[base.length];
          return b === '\\' || b === '/';
        }
        // The most specific (longest matching Path) rule governing cwd, or null. rules come from settings.json
        // AccountRules (PascalCase keys — Perch serialises its own settings that way).
        function ruleFor(cwd, rules) {
          const target = normPath(cwd);
          if (!target || !Array.isArray(rules)) return null;
          let best = null, bestLen = -1;
          for (const rule of rules) {
            const base = normPath(rule && rule.Path);
            if (!base || !atOrUnder(target, base)) continue;
            if (base.length > bestLen) { best = rule; bestLen = base.length; }
          }
          return best;
        }
        function allowedLabel(rule) {
          const names = ((rule && rule.Allowed) || []).map(a => (a && (a.Name || a.Email)) || '').filter(s => s && s.trim());
          if (names.length === 0) return 'the allowed account';
          return names.length === 1 ? names[0] : names[0] + ' +' + (names.length - 1);
        }
        // Reconcile the matched rule against the session's live account (org uuid). Alerting-only: a blank /
        // signed-out account is never a mismatch, matching AccountGuard.Evaluate.
        function guardrailFor(p, settings) {
          const out = { mismatch: false, expected: '', on: '' };
          try {
            const cwd = p.cwd || (p.workspace && p.workspace.current_dir) || '';
            const rule = ruleFor(cwd, settings.AccountRules);
            if (!rule || !Array.isArray(rule.Allowed) || rule.Allowed.length === 0) return out;
            const acct = readAccount();
            const live = acct.org_uuid || '';
            if (!live) return out;   // alerting-only: never flag a blank sign-in
            if (rule.Allowed.some(a => a && a.Uuid === live)) return out;   // on an allowed account → ok
            out.mismatch = true;
            out.expected = allowedLabel(rule);
            out.on = acct.org || acct.email || 'another account';
          } catch { }
          return out;
        }
        function injectPerch(p) {
          if (!NEED_PERCH || (p.perch !== undefined && p.perch !== null)) return;
          const s = readPerchSettings();
          p.perch = {
            context: {
              yellow: intOr(s.ContextPressureYellowPercent, 50),
              orange: intOr(s.ContextPressureOrangePercent, 65),
              red:    intOr(s.ContextPressureRedPercent, 80),
            },
            guardrail: guardrailFor(p, s),
          };
        }

        let payload = {};
        try { payload = JSON.parse(readFileSync(0, 'utf8') || '{}'); } catch { }
        try { injectGit(payload); } catch { }
        try { injectAccount(payload); } catch { }
        try { injectPerch(payload); } catch { }
        try { let out = render(TEMPLATE, payload); if (DEV) out = DEV_MARKER + out; process.stdout.write(out); } catch { }
        """;
}
