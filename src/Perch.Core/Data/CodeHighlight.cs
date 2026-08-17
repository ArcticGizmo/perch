namespace Perch.Data;

using System.Text;

/// <summary>The syntactic class of a highlighted span. <see cref="Plain"/> is everything uncategorised
/// (whitespace, operators, punctuation, ordinary identifiers) and carries the base code colour.</summary>
public enum CodeToken { Plain, Keyword, Type, Str, Number, Comment, Function }

/// <summary>
/// A tiny, dependency-free syntax highlighter for the Markdown preview's fenced code blocks. Given a fence
/// language tag (the <c>bash</c> in <c>```bash</c>) and the block text, it splits the text into coloured
/// spans — comments, strings, numbers, language keywords, known type/builtin names, function calls, and
/// plain text. It is deliberately approximate (a single generic tokenizer driven by per-language profiles,
/// not a real per-language lexer): good enough to make a snippet readable, cheap enough to run on every
/// preview re-render, and UI-free so it can live in the core and be unit-tested.
///
/// Two invariants the tests pin: the concatenation of every span's text equals the input exactly (nothing is
/// dropped or duplicated), and an unknown/blank language yields a single <see cref="CodeToken.Plain"/> span
/// (so it looks exactly like today's flat rendering).
/// </summary>
public static class CodeHighlight
{
    /// <summary>Split <paramref name="code"/> into (text, kind) spans using the profile for
    /// <paramref name="language"/>. Never throws; an unknown language returns one Plain span.</summary>
    public static IReadOnlyList<(string Text, CodeToken Kind)> Tokenize(string? language, string code)
    {
        var outp = new List<(string, CodeToken)>();
        if (string.IsNullOrEmpty(code))
            return outp;

        var profile = LangProfile.For(language);
        if (profile is null)
        {
            outp.Add((code, CodeToken.Plain));
            return outp;
        }

        int i = 0, n = code.Length;
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length > 0) { outp.Add((plain.ToString(), CodeToken.Plain)); plain.Clear(); }
        }
        void Emit(string s, CodeToken k) { FlushPlain(); outp.Add((s, k)); }

        while (i < n)
        {
            char c = code[i];

            // ── Block comments (e.g. /* … */, <!-- … -->). Unterminated runs to EOF. ──
            if (profile.BlockComments is { } bcs)
            {
                bool did = false;
                foreach (var (open, close) in bcs)
                {
                    if (!Match(code, i, open)) continue;
                    int end = code.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                    int stop = end < 0 ? n : end + close.Length;
                    Emit(code[i..stop], CodeToken.Comment);
                    i = stop; did = true; break;
                }
                if (did) continue;
            }

            // ── Line comments (#, //, --). Run to end of line. ──
            if (profile.LineComments is { } lcs)
            {
                bool did = false;
                foreach (var lc in lcs)
                {
                    if (!Match(code, i, lc)) continue;
                    int end = code.IndexOf('\n', i);
                    int stop = end < 0 ? n : end;
                    Emit(code[i..stop], CodeToken.Comment);
                    i = stop; did = true; break;
                }
                if (did) continue;
            }

            // ── Triple-quoted strings (Python """ / '''), which span lines. ──
            if (profile.TripleStrings && (Match(code, i, "\"\"\"") || Match(code, i, "'''")))
            {
                var q = code.Substring(i, 3);
                int end = code.IndexOf(q, i + 3, StringComparison.Ordinal);
                int stop = end < 0 ? n : end + 3;
                Emit(code[i..stop], CodeToken.Str);
                i = stop; continue;
            }

            // ── Ordinary strings. ──
            if (profile.StringDelims.IndexOf(c) >= 0)
            {
                int j = i + 1;
                while (j < n)
                {
                    if (profile.Escapes && code[j] == '\\' && j + 1 < n) { j += 2; continue; }
                    if (code[j] == c) { j++; break; }
                    // Backtick templates (JS/Go) legitimately span lines; quote strings end at the line.
                    if (code[j] == '\n' && c != '`') break;
                    j++;
                }
                Emit(code[i..j], CodeToken.Str);
                i = j; continue;
            }

            // ── Shell/PowerShell/PHP variables ($name, ${…}). ──
            if (profile.DollarVars && c == '$')
            {
                int j = i + 1;
                if (j < n && code[j] == '{')
                {
                    int close = code.IndexOf('}', j);
                    j = close < 0 ? n : close + 1;
                }
                else
                {
                    while (j < n && IsIdentPart(code[j])) j++;
                }
                if (j > i + 1) { Emit(code[i..j], CodeToken.Type); i = j; continue; }
                plain.Append('$'); i++; continue;
            }

            // ── Numbers (start on a digit; grab hex/float/underscore/suffix greedily). ──
            if (char.IsDigit(c))
            {
                int j = i + 1;
                while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] == '.' || code[j] == '_')) j++;
                Emit(code[i..j], CodeToken.Number);
                i = j; continue;
            }

            // ── Identifiers / keywords / types / function calls. ──
            if (IsIdentStart(c))
            {
                int j = i + 1;
                while (j < n && IsIdentPart(code[j])) j++;
                var word = code[i..j];
                CodeToken kind =
                    profile.Keywords.Contains(word) ? CodeToken.Keyword :
                    profile.Types.Contains(word)    ? CodeToken.Type :
                    IsCall(code, j)                 ? CodeToken.Function :
                                                      CodeToken.Plain;
                if (kind == CodeToken.Plain) plain.Append(word);
                else Emit(word, kind);
                i = j; continue;
            }

            // ── Anything else: operators, punctuation, whitespace. ──
            plain.Append(c);
            i++;
        }

        FlushPlain();
        return outp;
    }

    private static bool Match(string s, int i, string token) =>
        i + token.Length <= s.Length && string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    // A word immediately followed by '(' (ignoring inline spaces) reads as a function call/definition.
    private static bool IsCall(string s, int j)
    {
        while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
        return j < s.Length && s[j] == '(';
    }

    /// <summary>Per-language tokenizer configuration. Resolved from the fence tag; unknown tags yield null
    /// (no highlighting).</summary>
    private sealed class LangProfile
    {
        public string[]? LineComments;
        public (string Open, string Close)[]? BlockComments;
        public string StringDelims = "\"'";
        public bool Escapes = true;            // backslash escapes inside strings (C-family, JSON, …)
        public bool TripleStrings;             // Python-style triple-quoted strings
        public bool DollarVars;                // $name / ${…} variables (shells, PHP)
        public required HashSet<string> Keywords;
        public HashSet<string> Types = new(StringComparer.Ordinal);

        private static readonly (string Open, string Close)[] CBlock = [("/*", "*/")];

        // A C-family profile (//, /* */, "/'/` strings) with the given keyword/type sets.
        private static LangProfile CFamily(HashSet<string> kw, HashSet<string>? ty = null, string delims = "\"'`") =>
            new()
            {
                LineComments = ["//"], BlockComments = CBlock, StringDelims = delims,
                Keywords = kw, Types = ty ?? new(StringComparer.Ordinal),
            };

        private static HashSet<string> Set(params string[] words) => new(words, StringComparer.Ordinal);
        // Case-insensitive set, for languages whose keywords don't care about case (SQL, Dockerfile).
        private static HashSet<string> SetI(params string[] words) => new(words, StringComparer.OrdinalIgnoreCase);

        public static LangProfile? For(string? language)
        {
            var lang = (language ?? "").Trim().ToLowerInvariant();
            // A fence can carry extra info after the language ("ts jsx", "python title=x"); take the first token.
            int sp = lang.IndexOfAny([' ', '\t', ',', ';']);
            if (sp >= 0) lang = lang[..sp];

            return lang switch
            {
                "bash" or "sh" or "shell" or "zsh" or "console" or "shell-session" => Shell(),
                "powershell" or "pwsh" or "ps1" or "ps" => PowerShell(),
                "python" or "py" => Python(),
                "js" or "javascript" or "jsx" or "mjs" or "cjs" or "node" => JavaScript(),
                "ts" or "typescript" or "tsx" => TypeScript(),
                "cs" or "csharp" or "c#" or "dotnet" => CSharp(),
                "c" or "h" => C(),
                "cpp" or "c++" or "cc" or "cxx" or "hpp" => Cpp(),
                "go" or "golang" => Go(),
                "rust" or "rs" => Rust(),
                "java" => Java(),
                "kotlin" or "kt" => Kotlin(),
                "swift" => Swift(),
                "php" => Php(),
                "ruby" or "rb" => Ruby(),
                "sql" or "postgres" or "postgresql" or "mysql" or "sqlite" => Sql(),
                "json" or "json5" or "jsonc" => Json(),
                "yaml" or "yml" => Yaml(),
                "toml" or "ini" or "cfg" or "conf" or "dotenv" or "env" => Ini(),
                "css" or "scss" or "less" or "sass" => Css(),
                "html" or "xml" or "xhtml" or "svg" or "vue" => Markup(),
                "dockerfile" or "docker" => Dockerfile(),
                "makefile" or "make" or "mk" => Makefile(),
                _ => null,
            };
        }

        private static LangProfile Shell() => new()
        {
            LineComments = ["#"], StringDelims = "\"'`", Escapes = true, DollarVars = true,
            Keywords = Set("if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done",
                "case", "esac", "in", "function", "select", "time", "return", "break", "continue",
                "export", "local", "readonly", "declare", "typeset", "let", "eval", "exec", "trap",
                "set", "unset", "shift", "source"),
            Types = Set("echo", "printf", "read", "cd", "pwd", "ls", "cat", "grep", "sed", "awk", "cut",
                "sort", "uniq", "head", "tail", "find", "xargs", "test", "true", "false", "exit",
                "mkdir", "rm", "cp", "mv", "touch", "chmod", "chown", "curl", "wget", "git", "sudo",
                "docker", "kubectl", "npm", "node", "python", "pip", "dotnet"),
        };

        private static LangProfile PowerShell() => new()
        {
            LineComments = ["#"], BlockComments = [("<#", "#>")], StringDelims = "\"'`",
            Escapes = false, DollarVars = true,
            Keywords = Set("if", "else", "elseif", "switch", "foreach", "for", "while", "do", "until",
                "break", "continue", "return", "function", "filter", "param", "begin", "process", "end",
                "try", "catch", "finally", "throw", "trap", "class", "enum", "in", "throw"),
            Types = Set("Write-Host", "Write-Output", "Get-ChildItem", "Set-Location", "New-Item",
                "Remove-Item", "Get-Content", "Set-Content", "Where-Object", "ForEach-Object",
                "Select-Object", "Test-Path", "Start-Process", "Invoke-Expression"),
        };

        private static LangProfile Python() => new()
        {
            LineComments = ["#"], StringDelims = "\"'", Escapes = true, TripleStrings = true,
            Keywords = Set("def", "class", "return", "if", "elif", "else", "for", "while", "break",
                "continue", "pass", "import", "from", "as", "with", "try", "except", "finally", "raise",
                "yield", "lambda", "global", "nonlocal", "del", "assert", "async", "await", "in", "is",
                "not", "and", "or", "True", "False", "None", "match", "case"),
            Types = Set("int", "str", "float", "bool", "list", "dict", "set", "tuple", "bytes", "object",
                "print", "len", "range", "enumerate", "self", "cls", "super"),
        };

        private static LangProfile JavaScript() => CFamily(
            Set("var", "let", "const", "function", "return", "if", "else", "for", "while", "do", "switch",
                "case", "default", "break", "continue", "new", "delete", "typeof", "instanceof", "in", "of",
                "this", "class", "extends", "super", "import", "export", "from", "as", "async", "await",
                "yield", "try", "catch", "finally", "throw", "void", "null", "undefined", "true", "false"),
            Set("console", "Math", "JSON", "Object", "Array", "String", "Number", "Boolean", "Promise",
                "Map", "Set", "Symbol", "Date", "RegExp", "document", "window"));

        private static LangProfile TypeScript()
        {
            var p = JavaScript();
            foreach (var k in new[] { "interface", "type", "enum", "namespace", "declare", "public",
                "private", "protected", "readonly", "abstract", "implements", "keyof", "infer", "is" })
                p.Keywords.Add(k);
            foreach (var t in new[] { "string", "number", "boolean", "any", "unknown", "never", "void",
                "object", "bigint" })
                p.Types.Add(t);
            return p;
        }

        private static LangProfile CSharp() => CFamily(
            Set("using", "namespace", "class", "struct", "interface", "enum", "record", "public",
                "private", "protected", "internal", "static", "readonly", "const", "sealed", "abstract",
                "virtual", "override", "partial", "async", "await", "var", "new", "return", "if", "else",
                "for", "foreach", "while", "do", "switch", "case", "default", "break", "continue", "in",
                "out", "ref", "is", "as", "try", "catch", "finally", "throw", "using", "lock", "yield",
                "get", "set", "init", "null", "true", "false", "this", "base", "typeof", "nameof", "when"),
            Set("void", "int", "long", "short", "byte", "bool", "char", "string", "float", "double",
                "decimal", "object", "uint", "ulong", "ushort", "sbyte", "nint", "nuint", "dynamic",
                "Task", "List", "Dictionary", "IEnumerable", "Span"),
            "\"'");

        private static LangProfile C() => CFamily(
            Set("if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue",
                "return", "goto", "sizeof", "typedef", "struct", "union", "enum", "static", "const",
                "extern", "volatile", "register", "inline", "restrict"),
            Set("void", "int", "long", "short", "char", "float", "double", "unsigned", "signed",
                "size_t", "bool"),
            "\"'");

        private static LangProfile Cpp()
        {
            var p = C();
            foreach (var k in new[] { "class", "public", "private", "protected", "virtual", "override",
                "template", "typename", "namespace", "using", "new", "delete", "this", "try", "catch",
                "throw", "nullptr", "true", "false", "constexpr", "auto", "explicit", "friend", "operator" })
                p.Keywords.Add(k);
            foreach (var t in new[] { "string", "vector", "map", "set", "wchar_t", "uint8_t", "int32_t",
                "int64_t", "std" })
                p.Types.Add(t);
            return p;
        }

        private static LangProfile Go() => CFamily(
            Set("func", "package", "import", "var", "const", "type", "struct", "interface", "map", "chan",
                "go", "defer", "return", "if", "else", "for", "range", "switch", "case", "default", "select",
                "break", "continue", "fallthrough", "goto", "nil", "true", "false", "iota"),
            Set("string", "int", "int8", "int16", "int32", "int64", "uint", "uint8", "uint16", "uint32",
                "uint64", "byte", "rune", "float32", "float64", "bool", "error", "any"),
            "\"'`");

        private static LangProfile Rust() => CFamily(
            Set("fn", "let", "mut", "const", "static", "struct", "enum", "trait", "impl", "mod", "pub",
                "use", "crate", "self", "super", "return", "if", "else", "match", "for", "while", "loop",
                "break", "continue", "in", "where", "as", "dyn", "ref", "move", "async", "await", "unsafe",
                "true", "false", "Some", "None", "Ok", "Err"),
            Set("i8", "i16", "i32", "i64", "i128", "u8", "u16", "u32", "u64", "u128", "usize", "isize",
                "f32", "f64", "bool", "char", "str", "String", "Vec", "Option", "Result", "Box"),
            "\"'");

        private static LangProfile Java() => CFamily(
            Set("public", "private", "protected", "static", "final", "abstract", "class", "interface",
                "enum", "extends", "implements", "package", "import", "new", "return", "if", "else", "for",
                "while", "do", "switch", "case", "default", "break", "continue", "try", "catch", "finally",
                "throw", "throws", "this", "super", "instanceof", "synchronized", "volatile", "transient",
                "native", "void", "null", "true", "false", "var", "record", "sealed", "yield"),
            Set("int", "long", "short", "byte", "char", "boolean", "float", "double", "String", "Object",
                "Integer", "Boolean", "List", "Map", "Set"),
            "\"'");

        private static LangProfile Kotlin() => CFamily(
            Set("fun", "val", "var", "class", "object", "interface", "data", "sealed", "enum", "package",
                "import", "return", "if", "else", "when", "for", "while", "do", "break", "continue", "is",
                "as", "in", "out", "try", "catch", "finally", "throw", "this", "super", "null", "true",
                "false", "override", "open", "abstract", "private", "public", "protected", "internal",
                "companion", "init", "constructor", "suspend"),
            Set("Int", "Long", "Short", "Byte", "Char", "Boolean", "Float", "Double", "String", "Any",
                "Unit", "List", "Map", "Set", "Array"),
            "\"'");

        private static LangProfile Swift() => CFamily(
            Set("func", "let", "var", "class", "struct", "enum", "protocol", "extension", "import",
                "return", "if", "else", "guard", "switch", "case", "default", "for", "while", "repeat",
                "break", "continue", "in", "as", "is", "try", "catch", "throw", "throws", "defer", "self",
                "super", "nil", "true", "false", "private", "public", "internal", "fileprivate", "open",
                "static", "final", "override", "init", "deinit", "some", "any", "async", "await"),
            Set("Int", "Double", "Float", "Bool", "String", "Character", "Array", "Dictionary", "Set",
                "Optional", "Void"),
            "\"'");

        private static LangProfile Php() => new()
        {
            LineComments = ["//", "#"], BlockComments = CBlock, StringDelims = "\"'", Escapes = true,
            DollarVars = true,
            Keywords = Set("function", "class", "interface", "trait", "extends", "implements", "public",
                "private", "protected", "static", "final", "abstract", "const", "return", "if", "else",
                "elseif", "endif", "for", "foreach", "while", "do", "switch", "case", "default", "break",
                "continue", "new", "echo", "print", "namespace", "use", "as", "try", "catch", "finally",
                "throw", "instanceof", "null", "true", "false", "array", "global", "require", "include",
                "require_once", "include_once", "fn"),
            Types = Set("int", "float", "string", "bool", "void", "object", "mixed", "self", "parent"),
        };

        private static LangProfile Ruby() => new()
        {
            LineComments = ["#"], StringDelims = "\"'`", Escapes = true,
            Keywords = Set("def", "class", "module", "end", "if", "elsif", "else", "unless", "case", "when",
                "then", "for", "while", "until", "do", "begin", "rescue", "ensure", "retry", "return",
                "yield", "break", "next", "redo", "in", "and", "or", "not", "nil", "true", "false", "self",
                "super", "require", "require_relative", "attr_accessor", "attr_reader", "attr_writer",
                "lambda", "proc", "raise"),
            Types = Set("puts", "print", "p", "new", "Integer", "String", "Array", "Hash", "Symbol"),
        };

        private static LangProfile Sql() => new()
        {
            LineComments = ["--"], BlockComments = CBlock, StringDelims = "'\"", Escapes = false,
            Keywords = SetI("SELECT", "FROM", "WHERE", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE",
                "CREATE", "ALTER", "DROP", "TABLE", "VIEW", "INDEX", "JOIN", "INNER", "LEFT", "RIGHT",
                "OUTER", "FULL", "ON", "AND", "OR", "NOT", "NULL", "IS", "IN", "LIKE", "BETWEEN", "GROUP",
                "BY", "ORDER", "HAVING", "LIMIT", "OFFSET", "DISTINCT", "AS", "UNION", "ALL", "CASE", "WHEN",
                "THEN", "ELSE", "END", "PRIMARY", "KEY", "FOREIGN", "REFERENCES", "DEFAULT", "CONSTRAINT",
                "UNIQUE", "CHECK", "WITH", "RETURNING", "ASC", "DESC"),
            Types = SetI("INT", "INTEGER", "BIGINT", "SMALLINT", "VARCHAR", "CHAR", "TEXT", "BOOLEAN", "BOOL",
                "DATE", "TIMESTAMP", "TIME", "FLOAT", "DOUBLE", "DECIMAL", "NUMERIC", "SERIAL", "UUID",
                "JSON", "JSONB", "BLOB"),
        };

        private static LangProfile Json() => new()
        {
            StringDelims = "\"", Escapes = true,
            Keywords = Set("true", "false", "null"),
        };

        private static LangProfile Yaml() => new()
        {
            LineComments = ["#"], StringDelims = "\"'", Escapes = true,
            Keywords = Set("true", "false", "null", "yes", "no", "on", "off", "True", "False", "Null",
                "None", "~"),
        };

        private static LangProfile Ini() => new()
        {
            LineComments = ["#", ";"], StringDelims = "\"'", Escapes = false,
            Keywords = Set("true", "false", "null", "yes", "no", "on", "off"),
        };

        private static LangProfile Css() => new()
        {
            BlockComments = CBlock, StringDelims = "\"'", Escapes = true,
            Keywords = Set("important", "inherit", "initial", "unset", "auto", "none", "true", "false"),
        };

        private static LangProfile Markup() => new()
        {
            BlockComments = [("<!--", "-->")], StringDelims = "\"'", Escapes = false,
            Keywords = new HashSet<string>(StringComparer.Ordinal),
        };

        private static LangProfile Dockerfile() => new()
        {
            LineComments = ["#"], StringDelims = "\"'", Escapes = true,
            Keywords = SetI("FROM", "RUN", "CMD", "LABEL", "MAINTAINER", "EXPOSE", "ENV", "ADD", "COPY",
                "ENTRYPOINT", "VOLUME", "USER", "WORKDIR", "ARG", "ONBUILD", "STOPSIGNAL", "HEALTHCHECK",
                "SHELL", "AS"),
        };

        private static LangProfile Makefile() => new()
        {
            LineComments = ["#"], StringDelims = "\"'`", Escapes = false, DollarVars = true,
            Keywords = Set("ifeq", "ifneq", "ifdef", "ifndef", "else", "endif", "define", "endef",
                "include", "export", "unexport", "override", "vpath", ".PHONY"),
        };
    }
}
