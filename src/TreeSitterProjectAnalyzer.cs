using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MyAiGen;

public enum TsSymbolKind { Class, Interface, Struct, Enum, EnumMember, Record, Constructor, Method, Property, Field, Function, Delegate, Event, Operator, Destructor, Indexer, Package, Type, Const, Var, Trait, Union, Module, Static, Macro }

public sealed class TsSymbol
{
    public TsSymbolKind Kind;
    public string Name = "";
    public string Accessibility = "";
    public int StartLine;
    public int EndLine;
}

public enum TsRefKind { Declaration, Usage }

public sealed class TsRef
{
    public string RelativePath = "";
    public int Line;
    public int Column;
    public TsRefKind Kind;
    public string Context = "";
}

public sealed class TsMethod
{
    public string RelativePath = "";
    public string Name = "";
    public string Signature = "";
    public int StartLine;
    public int EndLine;
    public string Lang = "";
    public int ParamCount = -1;
    public bool EmptyBody;
}

/// <summary>A declaration site from the defs/symbol commands: any symbol kind.</summary>
public sealed class TsDef
{
    public string RelativePath = "";
    public int Line;
    public int Column;
    public int EndLine;
    public string NodeType = "";
    public string Context = "";
    public string Lang = "";
    public string Signature = "";
    public int ParamCount = -1;
    public string Container = "";
    public string ContainerKind = "";
    public List<string> Heritage = new();
    public List<string> Modifiers = new();
}

/// <summary>A usage site with the callable that contains it (callers/symbol).</summary>
public sealed class TsCaller
{
    public string RelativePath = "";
    public int Line;
    public int Column;
    public string Context = "";
    public string Caller = "";
    public int CallerLine;
}

/// <summary>An override/implementation entry (impls/symbol): one declaration of a
/// method-like symbol plus its container and that container's heritage.</summary>
public sealed class TsImpl
{
    public string RelativePath = "";
    public int Line;
    public string Kind = "";
    public string Container = "";
    public string ContainerKind = "";
    public List<string> Heritage = new();
    public List<string> Modifiers = new();
}

/// <summary>Combined symbol report: definitions + callers + implementations.</summary>
public sealed class TsSymbolReport
{
    public List<TsDef> Definitions = new();
    public List<TsCaller> Callers = new();
    public List<TsImpl> Implementations = new();
}

/// <summary>One definition site from the global symbol table (symbols command).</summary>
public sealed class TsSymbolSite
{
    public string RelativePath = "";
    public int Line;
    public string NodeType = "";
}

/// <summary>
/// C# side of ts_project.py — project-wide tree-sitter analysis, batched into a single
/// process spawn per operation (as opposed to TreeSitterChecker, which runs once per
/// write_file on one file). Capabilities:
///
///   IndexProject        — precise per-file symbol extraction for every supported
///                         language (see SupportedExtensions). Used by ProjectIndex to
///                         replace regex-based SymbolPatterns extraction.
///
///   FindReferences      — precise project-wide reference search for one symbol name,
///                         with declaration vs. usage already classified.
///
///   FindMethods         — precise definitions of one symbol name (signature + body
///                         span), used by analyze_method.
///
///   FindDefinitions     — every declaration site of a symbol (any kind: classes,
///                         fields, parameters...), not just callables.
///
///   FindCallers         — every usage site with the enclosing callable ("who calls X").
///
///   FindImplementations — every declaration of a method name with its container type
///                         and that container's base/interface list — the syntactic
///                         override/implementation picture.
///
///   ListSymbols         — the project-wide symbol table (unique names -> definition
///                         sites), optional substring filter.
///
///   SearchMethods       — structural search: method definitions by name filtered on
///                         parameter count, with empty-body flag.
///
///   FindSymbol          — the combined report (definitions + callers +
///                         implementations) from ONE process spawn — the backing for
///                         both analyze_method and the find_symbol agent tool.
///
///   CheckFile           — single-file syntax scan for any supported language, used by
///                         the write_file loop (ts_check.py remains the C#-specific
///                         checker with placeholder/dead-code extras).
///
/// All are advisory: any failure (missing embedded python, timeout, bad JSON, project
/// too large, etc.) returns null and the caller falls back to its prior regex-based
/// behavior — this never turns into a hard error.
///
/// SYNTACTIC, not semantic — see ts_project.py's module docstring for the same caveat
/// this class inherits: no overload resolution, no cross-type disambiguation.
/// </summary>
public static class TreeSitterProjectAnalyzer
{
    /// <summary>File extensions ts_project.py parses; anything else is regex-only.</summary>
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".go", ".rs" };
    // Project-wide operations walk every .cs file, so they're inherently slower than the
    // single-file write_file check — generous timeout, but still bounded so a huge/odd
    // project can't hang an agent turn indefinitely.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);
    private static readonly int MaxErrorsShown = 40;

    private static bool? _available;

    private static string PythonExePath => Path.Combine(AppContext.BaseDirectory, "Python", "python.exe");
    private static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "Python", "ts_project.py");

    public static bool IsAvailable()
    {
        _available ??= File.Exists(PythonExePath) && File.Exists(ScriptPath);
        return _available.Value;
    }

    public static void ResetAvailabilityCache() => _available = null;

    /// <summary>Parses every supported-language file under projectPath and returns its
    /// declared symbols, keyed by path relative to projectPath (forward-slash
    /// separated, matching the convention ts_project.py emits). Returns null on any
    /// failure — caller should fall back to its own per-file extraction in that case.</summary>
    public static Dictionary<string, List<TsSymbol>>? IndexProject(string projectPath)
    {
        var stdout = RunProjectScript("index", projectPath);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("files", out var filesProp)) return null;

            var result = new Dictionary<string, List<TsSymbol>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fileEntry in filesProp.EnumerateObject())
            {
                var symbols = new List<TsSymbol>();
                if (fileEntry.Value.TryGetProperty("symbols", out var symbolsProp))
                {
                    foreach (var s in symbolsProp.EnumerateArray())
                    {
                        var kindStr = s.TryGetProperty("kind", out var k) ? k.GetString() : null;
                        if (!TryParseKind(kindStr, out var kind)) continue;
                        symbols.Add(new TsSymbol
                        {
                            Kind = kind,
                            Name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            Accessibility = s.TryGetProperty("accessibility", out var a) ? a.GetString() ?? "" : "",
                            StartLine = s.TryGetProperty("start_line", out var sl) ? sl.GetInt32() : 0,
                            EndLine = s.TryGetProperty("end_line", out var el) ? el.GetInt32() : 0,
                        });
                    }
                }
                result[fileEntry.Name] = symbols;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds every identifier node matching symbolName across all supported
    /// language files under projectPath, classified as declaration or usage. Returns
    /// null on any failure.</summary>
    public static List<TsRef>? FindReferences(string projectPath, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return null;
        var stdout = RunProjectScript("refs", projectPath, symbolName);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("matches", out var matchesProp)) return null;

            var result = new List<TsRef>();
            foreach (var m in matchesProp.EnumerateArray())
            {
                var kindStr = m.TryGetProperty("kind", out var k) ? k.GetString() : "usage";
                result.Add(new TsRef
                {
                    RelativePath = m.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    Line = m.TryGetProperty("line", out var l) ? l.GetInt32() : 0,
                    Column = m.TryGetProperty("column", out var c) ? c.GetInt32() : 0,
                    Kind = kindStr == "declaration" ? TsRefKind.Declaration : TsRefKind.Usage,
                    Context = m.TryGetProperty("context", out var ctx) ? ctx.GetString() ?? "" : "",
                });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Finds every definition of symbolName across all supported language files
    /// under projectPath, with its signature and body span. Returns null on any failure.</summary>
    public static List<TsMethod>? FindMethods(string projectPath, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return null;
        var stdout = RunProjectScript("methods", projectPath, symbolName);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("matches", out var matchesProp)) return null;

            var result = new List<TsMethod>();
            foreach (var m in matchesProp.EnumerateArray())
            {
                result.Add(new TsMethod
                {
                    RelativePath = m.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Signature = m.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "",
                    StartLine = m.TryGetProperty("start_line", out var sl) ? sl.GetInt32() : 0,
                    EndLine = m.TryGetProperty("end_line", out var el) ? el.GetInt32() : 0,
                    Lang = m.TryGetProperty("lang", out var l) ? l.GetString() ?? "" : "",
                });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every declaration site of symbolName (any symbol kind, not just
    /// callables) — go-to-definition across the project. Returns null on failure.</summary>
    public static List<TsDef>? FindDefinitions(string projectPath, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return null;
        var stdout = RunProjectScript("defs", projectPath, symbolName);
        return ParseDefs(stdout);
    }

    /// <summary>Every usage site of symbolName with the enclosing callable — "who
    /// calls X" grouped by caller. Returns null on failure.</summary>
    public static List<TsCaller>? FindCallers(string projectPath, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return null;
        var stdout = RunProjectScript("callers", projectPath, symbolName);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("matches", out var matchesProp)) return null;

            var result = new List<TsCaller>();
            foreach (var m in matchesProp.EnumerateArray())
            {
                result.Add(new TsCaller
                {
                    RelativePath = m.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    Line = m.TryGetProperty("line", out var l) ? l.GetInt32() : 0,
                    Column = m.TryGetProperty("column", out var c) ? c.GetInt32() : 0,
                    Context = m.TryGetProperty("context", out var ctx) ? ctx.GetString() ?? "" : "",
                    Caller = m.TryGetProperty("caller", out var cl) ? cl.GetString() ?? "" : "",
                    CallerLine = m.TryGetProperty("caller_line", out var cll) ? cll.GetInt32() : 0,
                });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every declaration of a method-like symbol with its container and that
    /// container's heritage — the syntactic override/implementation picture. Returns
    /// null on failure.</summary>
    public static List<TsImpl>? FindImplementations(string projectPath, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return null;
        var stdout = RunProjectScript("impls", projectPath, symbolName);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("matches", out var matchesProp)) return null;

            var result = new List<TsImpl>();
            foreach (var m in matchesProp.EnumerateArray())
            {
                var impl = new TsImpl
                {
                    RelativePath = m.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    Line = m.TryGetProperty("line", out var l) ? l.GetInt32() : 0,
                    Kind = m.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                    Container = m.TryGetProperty("container", out var c) ? c.GetString() ?? "" : "",
                    ContainerKind = m.TryGetProperty("container_kind", out var ck) ? ck.GetString() ?? "" : "",
                };
                if (m.TryGetProperty("heritage", out var h) && h.ValueKind == JsonValueKind.Array)
                    foreach (var x in h.EnumerateArray())
                        impl.Heritage.Add(x.GetString() ?? "");
                if (m.TryGetProperty("modifiers", out var mo) && mo.ValueKind == JsonValueKind.Array)
                    foreach (var x in mo.EnumerateArray())
                        impl.Modifiers.Add(x.GetString() ?? "");
                result.Add(impl);
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The project-wide symbol table: every unique declared name with up to
    /// three definition sites each. Optional case-insensitive substring filter.
    /// Returns null on failure.</summary>
    public static Dictionary<string, List<TsSymbolSite>>? ListSymbols(string projectPath, string? substring = null)
    {
        var stdout = RunProjectScript("symbols", projectPath, substring);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("symbols", out var symbolsProp)) return null;

            var result = new Dictionary<string, List<TsSymbolSite>>();
            foreach (var nameEntry in symbolsProp.EnumerateObject())
            {
                var sites = new List<TsSymbolSite>();
                foreach (var s in nameEntry.Value.EnumerateArray())
                {
                    sites.Add(new TsSymbolSite
                    {
                        RelativePath = s.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                        Line = s.TryGetProperty("line", out var l) ? l.GetInt32() : 0,
                        NodeType = s.TryGetProperty("node_type", out var nt) ? nt.GetString() ?? "" : "",
                    });
                }
                result[nameEntry.Name] = sites;
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Structural search: method/function definitions named symbolName with a
    /// parameter count in [minParams, maxParams] (null = unbounded). Returns null on
    /// failure.</summary>
    public static List<TsMethod>? SearchMethods(string projectPath, string symbolName, int? minParams = null, int? maxParams = null)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return null;
        var args = symbolName;
        if (minParams.HasValue) args += " " + minParams.Value;
        if (maxParams.HasValue) args += " " + maxParams.Value;
        var stdout = RunProjectScript("search", projectPath, args);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("matches", out var matchesProp)) return null;

            var result = new List<TsMethod>();
            foreach (var m in matchesProp.EnumerateArray())
            {
                result.Add(new TsMethod
                {
                    RelativePath = m.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Signature = m.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "",
                    StartLine = m.TryGetProperty("start_line", out var sl) ? sl.GetInt32() : 0,
                    EndLine = m.TryGetProperty("end_line", out var el) ? el.GetInt32() : 0,
                    Lang = m.TryGetProperty("lang", out var l) ? l.GetString() ?? "" : "",
                    ParamCount = m.TryGetProperty("param_count", out var pc) ? pc.GetInt32() : -1,
                    EmptyBody = m.TryGetProperty("empty_body", out var eb) && eb.GetBoolean(),
                });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Structural search across ALL callables: every method/function in
    /// the project with a parameter count in [minParams, maxParams] (null =
    /// unbounded), regardless of name. Returns null on failure.</summary>
    public static List<TsMethod>? SearchMethodsAny(string projectPath, int? minParams = null, int? maxParams = null)
    {
        var args = "*";
        if (minParams.HasValue) args += " " + minParams.Value;
        if (maxParams.HasValue) args += " " + maxParams.Value;
        var stdout = RunProjectScript("search", projectPath, args);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("matches", out var matchesProp)) return null;

            var result = new List<TsMethod>();
            foreach (var m in matchesProp.EnumerateArray())
            {
                result.Add(new TsMethod
                {
                    RelativePath = m.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    Name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Signature = m.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "",
                    StartLine = m.TryGetProperty("start_line", out var sl) ? sl.GetInt32() : 0,
                    EndLine = m.TryGetProperty("end_line", out var el) ? el.GetInt32() : 0,
                    Lang = m.TryGetProperty("lang", out var l) ? l.GetString() ?? "" : "",
                    ParamCount = m.TryGetProperty("param_count", out var pc) ? pc.GetInt32() : -1,
                    EmptyBody = m.TryGetProperty("empty_body", out var eb) && eb.GetBoolean(),
                });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Combined symbol report — definitions, callers and implementations for
    /// one symbol name from a single process spawn. minParams/maxParams optionally
    /// restrict the callable definitions by parameter count. Returns null on failure.</summary>
    public static TsSymbolReport? FindSymbol(string projectPath, string symbolName, int? minParams = null, int? maxParams = null)
    {
        if (string.IsNullOrWhiteSpace(symbolName)) return null;
        var args = symbolName;
        if (minParams.HasValue) args += " " + minParams.Value;
        if (maxParams.HasValue) args += " " + maxParams.Value;
        var stdout = RunProjectScript("symbol", projectPath, args);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;

            var report = new TsSymbolReport();
            if (root.TryGetProperty("definitions", out var defsProp) && defsProp.ValueKind == JsonValueKind.Array)
                foreach (var d in defsProp.EnumerateArray())
                    report.Definitions.Add(ParseDef(d));

            if (root.TryGetProperty("callers", out var callersProp) && callersProp.ValueKind == JsonValueKind.Array)
                foreach (var m in callersProp.EnumerateArray())
                    report.Callers.Add(new TsCaller
                    {
                        RelativePath = m.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                        Line = m.TryGetProperty("line", out var l) ? l.GetInt32() : 0,
                        Column = m.TryGetProperty("column", out var c) ? c.GetInt32() : 0,
                        Context = m.TryGetProperty("context", out var ctx) ? ctx.GetString() ?? "" : "",
                        Caller = m.TryGetProperty("caller", out var cl) ? cl.GetString() ?? "" : "",
                        CallerLine = m.TryGetProperty("caller_line", out var cll) ? cll.GetInt32() : 0,
                    });

            if (root.TryGetProperty("implementations", out var implsProp) && implsProp.ValueKind == JsonValueKind.Array)
                foreach (var m in implsProp.EnumerateArray())
                {
                    var impl = new TsImpl
                    {
                        RelativePath = m.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                        Line = m.TryGetProperty("line", out var l) ? l.GetInt32() : 0,
                        Kind = m.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "",
                        Container = m.TryGetProperty("container", out var c) ? c.GetString() ?? "" : "",
                        ContainerKind = m.TryGetProperty("container_kind", out var ck) ? ck.GetString() ?? "" : "",
                    };
                    if (m.TryGetProperty("heritage", out var h) && h.ValueKind == JsonValueKind.Array)
                        foreach (var x in h.EnumerateArray())
                            impl.Heritage.Add(x.GetString() ?? "");
                    if (m.TryGetProperty("modifiers", out var mo) && mo.ValueKind == JsonValueKind.Array)
                        foreach (var x in mo.EnumerateArray())
                            impl.Modifiers.Add(x.GetString() ?? "");
                    report.Implementations.Add(impl);
                }

            return report;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Runs the generic single-file syntax scan (ts_project.py check) against
    /// a just-written file of any supported language. Returns null when there is
    /// nothing worth telling the model (clean parse, checker unavailable/errored,
    /// timed out); a tool-result-ready findings block otherwise. Never throws.</summary>
    public static string? CheckFile(string fullPath)
    {
        if (!IsAvailable()) return null;
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return null;

        var stdout = RunCheckScript(fullPath);
        if (stdout == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp)) return null;
            if (okProp.ValueKind != JsonValueKind.True && okProp.ValueKind != JsonValueKind.False)
                return null;
            if (!root.TryGetProperty("errors", out var errorsProp) || errorsProp.GetArrayLength() == 0)
                return null;

            var total = errorsProp.GetArrayLength();
            var sb = new StringBuilder();
            sb.Append("\n[tree-sitter syntax check] ").Append(total)
              .Append(total == 1 ? " issue found" : " issues found")
              .Append(" — these are real syntax problems, located instantly with no build needed:\n");

            var shown = 0;
            foreach (var err in errorsProp.EnumerateArray())
            {
                if (shown >= MaxErrorsShown)
                {
                    sb.Append("  ... ").Append(total - shown).Append(" more (fix these first, then re-check)\n");
                    break;
                }
                var line = err.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                var col = err.TryGetProperty("column", out var c) ? c.GetInt32() : 0;
                var kind = err.TryGetProperty("kind", out var k) ? k.GetString() : "ERROR";
                var nodeType = err.TryGetProperty("node_type", out var nt) ? nt.GetString() : "";
                var text = err.TryGetProperty("text", out var t) ? t.GetString() : "";
                sb.Append("  - line ").Append(line).Append(", col ").Append(col).Append(": ");
                sb.Append(kind == "MISSING"
                    ? $"missing '{nodeType}'"
                    : $"unexpected/invalid syntax near \"{text}\"");
                sb.Append('\n');
                shown++;
            }
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static List<TsDef>? ParseDefs(string? stdout)
    {
        if (stdout == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp) || okProp.ValueKind != JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("matches", out var matchesProp)) return null;

            var result = new List<TsDef>();
            foreach (var m in matchesProp.EnumerateArray())
                result.Add(ParseDef(m));
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static TsDef ParseDef(JsonElement d)
    {
        return new TsDef
        {
            RelativePath = d.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
            Line = d.TryGetProperty("line", out var l) ? l.GetInt32() : 0,
            Column = d.TryGetProperty("column", out var c) ? c.GetInt32() : 0,
            EndLine = d.TryGetProperty("end_line", out var el) ? el.GetInt32() : 0,
            NodeType = d.TryGetProperty("node_type", out var nt) ? nt.GetString() ?? "" : "",
            Context = d.TryGetProperty("context", out var ctx) ? ctx.GetString() ?? "" : "",
            Lang = d.TryGetProperty("lang", out var lg) ? lg.GetString() ?? "" : "",
            Signature = d.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "",
            ParamCount = d.TryGetProperty("param_count", out var pc) ? pc.GetInt32() : -1,
            Container = d.TryGetProperty("container", out var c2) ? c2.GetString() ?? "" : "",
            ContainerKind = d.TryGetProperty("container_kind", out var ck) ? ck.GetString() ?? "" : "",
            Heritage = d.TryGetProperty("heritage", out var h) ? ParseStringArray(h) : new List<string>(),
            Modifiers = d.TryGetProperty("modifiers", out var mo) ? ParseStringArray(mo) : new List<string>(),
        };
    }

    private static List<string> ParseStringArray(JsonElement e)
    {
        var result = new List<string>();
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var item in e.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
        return result;
    }

    private static bool TryParseKind(string? kindStr, out TsSymbolKind kind)
    {
        switch (kindStr)
        {
            case "class": kind = TsSymbolKind.Class; return true;
            case "interface": kind = TsSymbolKind.Interface; return true;
            case "struct": kind = TsSymbolKind.Struct; return true;
            case "enum": kind = TsSymbolKind.Enum; return true;
            case "enum_member": kind = TsSymbolKind.EnumMember; return true;
            case "record": kind = TsSymbolKind.Record; return true;
            case "constructor": kind = TsSymbolKind.Constructor; return true;
            case "method": kind = TsSymbolKind.Method; return true;
            case "property": kind = TsSymbolKind.Property; return true;
            case "field": kind = TsSymbolKind.Field; return true;
            case "function": kind = TsSymbolKind.Function; return true;
            case "delegate": kind = TsSymbolKind.Delegate; return true;
            case "event": kind = TsSymbolKind.Event; return true;
            case "operator": kind = TsSymbolKind.Operator; return true;
            case "destructor": kind = TsSymbolKind.Destructor; return true;
            case "indexer": kind = TsSymbolKind.Indexer; return true;
            case "package": kind = TsSymbolKind.Package; return true;
            case "type": kind = TsSymbolKind.Type; return true;
            case "const": kind = TsSymbolKind.Const; return true;
            case "var": kind = TsSymbolKind.Var; return true;
            case "trait": kind = TsSymbolKind.Trait; return true;
            case "union": kind = TsSymbolKind.Union; return true;
            case "module": kind = TsSymbolKind.Module; return true;
            case "static": kind = TsSymbolKind.Static; return true;
            case "macro": kind = TsSymbolKind.Macro; return true;
            default: kind = default; return false;
        }
    }

    private static string? RunProjectScript(string command, string projectPath, string? extraArg = null)
    {
        if (!IsAvailable()) return null;
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PythonExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(ScriptPath);
            psi.ArgumentList.Add(command);
            psi.ArgumentList.Add(projectPath);
            if (extraArg != null) psi.ArgumentList.Add(extraArg);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            // One JSON line expected, but tolerate stray stdout noise the same way
            // TreeSitterChecker does.
            var jsonLine = stdout.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{'));
            return jsonLine;
        }
        catch
        {
            return null;
        }
    }

    private static string? RunCheckScript(string fullPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PythonExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(ScriptPath);
            psi.ArgumentList.Add("check");
            psi.ArgumentList.Add(fullPath);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)CheckTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(stdout)) return null;
            return stdout.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{'));
        }
        catch
        {
            return null;
        }
    }
}
