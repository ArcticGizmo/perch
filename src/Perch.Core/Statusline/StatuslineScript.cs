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
    /// baked in). Throws if the profile has no template.</summary>
    public static string Generate(StatuslineProfile profile)
    {
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

        return Body
            .Replace("@@TEMPLATE@@", templateLiteral)
            .Replace("@@NAME@@", nameLiteral)
            .Replace("@@GITCOUNTS@@", needsCounts ? "true" : "false");
    }

    // The script body. @@TEMPLATE@@ / @@NAME@@ are replaced with JSON string literals. Kept as a raw
    // string literal so backslashes (\x1b, regex escapes) are literal. Everything is defensive: any
    // failure prints nothing rather than a stack trace into the status bar.
    private const string Body = """
        #!/usr/bin/env node
        // Perch statusline — standalone, generated from profile @@NAME@@.
        // Self-contained: edit freely; no Perch process is involved at runtime.
        import { readFileSync, existsSync, statSync } from 'node:fs';
        import { join, dirname, isAbsolute, resolve } from 'node:path';
        import { execFileSync } from 'node:child_process';

        const TEMPLATE = @@TEMPLATE@@;
        const NEED_GIT_COUNTS = @@GITCOUNTS@@;

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
        const ansi = (t, c) => {
          const rgb = c ? (COL[c] || hex(c)) : null;
          return rgb ? `\x1b[38;2;${rgb[0]};${rgb[1]};${rgb[2]}m${t}\x1b[0m` : t;
        };
        const sepText = (body) => body === 'sep' ? ' | '
          : body.startsWith('sep:') ? ' ' + (body.slice(4).trim() || '|') + ' ' : null;

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
              case 'round': text = String(Math.round(n || 0)); break;
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
            else { const sp = sepText(body); if (sp !== null) out.push({ t: 'sep', v: sp }); else out.push({ t: 'var', v: body }); }
            last = re.lastIndex;
          }
          if (last < t.length) out.push({ t: 'text', v: t.slice(last) });
          return out;
        }
        function build(toks) {
          let i = 0;
          const walk = () => {
            const nodes = [];
            while (i < toks.length) {
              const tk = toks[i];
              if (tk.t === 'close') { i++; return nodes; }
              if (tk.t === 'open' || tk.t === 'inv') { i++; nodes.push({ kind: tk.t, v: tk.v, kids: walk() }); }
              else { nodes.push(tk); i++; }
            }
            return nodes;
          };
          return walk();
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
        // Render to an array of pieces {text,color,isSep} so the {{sep}} collapse can drop empty cells.
        function renderPieces(nodes, ctx) {
          const pieces = [];
          const walk = (ns) => {
            for (const n of ns) {
              if (n.t === 'text') { if (n.v.length) pieces.push({ text: n.v, color: null, isSep: false }); }
              else if (n.t === 'var') { const { text, color } = applyVar(n.v, ctx); if (text.length) pieces.push({ text, color, isSep: false }); }
              else if (n.t === 'sep') pieces.push({ text: n.v, color: null, isSep: true });
              else if (n.kind === 'open') {
                let cond;
                if (n.v.startsWith('if ')) cond = evalExpr(n.v.slice(3), ctx);
                else if (n.v.startsWith('unless ')) cond = !evalExpr(n.v.slice(7), ctx);
                else cond = evalTruthy(n.v, ctx);
                if (cond) walk(n.kids);
              } else if (n.kind === 'inv') {
                if (!evalTruthy(n.v, ctx)) walk(n.kids);
              }
            }
          };
          walk(nodes);
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
        // A backslash before a newline is a soft break (editor readability wrap) — drop it so it doesn't
        // split the line. Mirrors StatuslineTemplate.StripSoftBreaks.
        const render = (tpl, ctx) => {
          const pieces = collapse(renderPieces(build(tokenize(tpl.replace(/\\\r?\n/g, ''))), ctx));
          let out = '';
          for (const p of pieces) out += ansi(p.text, p.color);
          return out;
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

        let payload = {};
        try { payload = JSON.parse(readFileSync(0, 'utf8') || '{}'); } catch { }
        try { injectGit(payload); } catch { }
        try { process.stdout.write(render(TEMPLATE, payload)); } catch { }
        """;
}
