using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MyAiGen;

/// <summary>
/// Fast, build-free C# syntax validation for the agentic write_file loop, powered by
/// an embedded Python interpreter + tree-sitter (tree-sitter-c-sharp grammar).
/// Tree-sitter uses error-recovery parsing, so it still produces a tree for broken
/// code and marks exactly where things went wrong (ERROR nodes) or what's missing
/// (MISSING nodes, e.g. an expected ';' or '}') — in milliseconds, with zero MSBuild
/// involvement. This gives the agent instant feedback on nearly every .cs write
/// instead of spending a full run_command + dotnet build cycle just to discover a
/// stray brace or a dropped semicolon.
///
/// Strictly advisory: this NEVER blocks, fails, or alters a write_file call. It only
/// appends a short findings block to the write_file result string when the freshly
/// written file has syntax issues. If the embedded interpreter, the checker script,
/// or the tree-sitter/tree-sitter-c-sharp packages aren't present, every call is a
/// silent no-op — old installs without ./Python behave exactly as before.
///
/// Expected layout (bundled next to the app, NOT inside the user's project):
///   &lt;app dir&gt;/Python/python.exe
///   &lt;app dir&gt;/Python/ts_check.py
/// The embedded python needs `tree-sitter` and `tree-sitter-c-sharp` importable
/// (embeddable Python ships without pip — see the header comment in ts_check.py
/// for the one-time setup: get-pip.py, then
/// `Python\python.exe -m pip install tree-sitter tree-sitter-c-sharp`).
/// </summary>
public static class TreeSitterChecker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly int MaxErrorsShown = 40;

    // Mirror AgenticWorkflow.TreeSitterSyntaxCheckEnabled — set from AppSettings at
    // agent-turn setup. Syntax errors have no toggle of their own (that's the base
    // EnableTreeSitterCheck flag gating CheckCSharpFile itself); these two let the
    // placeholder/dead-code sections be silenced independently since they're more
    // heuristic (dead-code especially, given the partial-class blind spot) than the
    // syntax check, which is unambiguous.
    public static bool EnablePlaceholderCheck = true;
    public static bool EnableDeadCodeCheck = true;

    // Probed once per process, not once per write_file call — a missing embedded
    // python shouldn't mean a filesystem stat on every single .cs write.
    private static bool? _available;

    private static string PythonExePath => Path.Combine(AppContext.BaseDirectory, "Python", "python.exe");
    private static string ScriptPath => Path.Combine(AppContext.BaseDirectory, "Python", "ts_check.py");

    public static bool IsAvailable()
    {
        _available ??= File.Exists(PythonExePath) && File.Exists(ScriptPath);
        return _available.Value;
    }

    /// <summary>Forces the next IsAvailable() check to re-probe disk — call this if the
    /// user installs the embedded interpreter mid-session without restarting the app.</summary>
    public static void ResetAvailabilityCache() => _available = null;

    /// <summary>Runs the tree-sitter syntax check against a C# file that was just
    /// written to disk. Returns null when there is nothing worth telling the model
    /// (clean parse, checker unavailable, checker itself errored, or timed out) — in
    /// every one of those cases the caller should leave its own message untouched.
    /// Returns a short, tool-result-ready findings block otherwise.</summary>
    public static string? CheckCSharpFile(string fullPath)
    {
        if (!IsAvailable()) return null;
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return null;

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
            psi.ArgumentList.Add(fullPath);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null; // advisory-only — a hung checker must never stall write_file
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            return ParseCheckerOutput(stdout);
        }
        catch
        {
            // Any failure here (interpreter missing/corrupt, permissions, whatever) must
            // never surface as a write_file error — the file write already succeeded.
            return null;
        }
    }

    private static string? ParseCheckerOutput(string stdout)
    {
        // The script always prints exactly one JSON line, but tolerate stray noise
        // (e.g. a Python deprecation warning) by taking the last line that looks like
        // a JSON object rather than assuming stdout is pure JSON.
        var jsonLine = stdout.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{'));
        if (jsonLine == null) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonLine); }
        catch { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var okProp)) return null;

            // ok:null means the checker itself couldn't run (missing deps, grammar load
            // failure, unreadable file, etc.) — that's not a verdict on the file either
            // way, so stay silent rather than falsely imply clean or broken.
            if (okProp.ValueKind != JsonValueKind.True && okProp.ValueKind != JsonValueKind.False)
                return null;

            var sb = new StringBuilder();

            if (root.TryGetProperty("errors", out var errorsProp) && errorsProp.GetArrayLength() > 0)
                AppendErrors(sb, errorsProp);

            if (EnablePlaceholderCheck &&
                root.TryGetProperty("placeholders", out var placeholdersProp) && placeholdersProp.GetArrayLength() > 0)
                AppendPlaceholders(sb, placeholdersProp);

            if (EnableDeadCodeCheck &&
                root.TryGetProperty("dead_code", out var deadCodeProp) && deadCodeProp.GetArrayLength() > 0)
                AppendDeadCode(sb, deadCodeProp);

            return sb.Length == 0 ? null : sb.ToString();
        }
    }

    private static void AppendErrors(StringBuilder sb, JsonElement errorsProp)
    {
        var total = errorsProp.GetArrayLength();
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
    }

    private static void AppendPlaceholders(StringBuilder sb, JsonElement placeholdersProp)
    {
        var total = placeholdersProp.GetArrayLength();
        sb.Append("\n[tree-sitter placeholder check] ").Append(total)
          .Append(total == 1 ? " incomplete-code marker found" : " incomplete-code markers found")
          .Append(" — placeholders are banned, finish these before calling the task done:\n");

        foreach (var ph in placeholdersProp.EnumerateArray())
        {
            var line = ph.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
            var col = ph.TryGetProperty("column", out var c) ? c.GetInt32() : 0;
            var kind = ph.TryGetProperty("kind", out var k) ? k.GetString() : "";
            var detail = ph.TryGetProperty("detail", out var d) ? d.GetString() : "";

            var label = kind switch
            {
                "todo_comment" => "TODO/placeholder comment",
                "not_implemented" => "NotImplementedException",
                "empty_body" => "empty body",
                _ => kind,
            };
            sb.Append("  - line ").Append(line).Append(", col ").Append(col)
              .Append(": ").Append(label).Append(" — ").Append(detail).Append('\n');
        }
    }

    private static void AppendDeadCode(StringBuilder sb, JsonElement deadCodeProp)
    {
        var total = deadCodeProp.GetArrayLength();
        sb.Append("\n[tree-sitter dead-code check] ").Append(total)
          .Append(total == 1 ? " unused private member found" : " unused private members found")
          .Append(" (this file only — a partial class's other files aren't visible here, and this is a lead to verify, not proof — confirm before deleting):\n");

        foreach (var dc in deadCodeProp.EnumerateArray())
        {
            var line = dc.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
            var col = dc.TryGetProperty("column", out var c) ? c.GetInt32() : 0;
            var name = dc.TryGetProperty("name", out var n) ? n.GetString() : "";
            var kind = dc.TryGetProperty("kind", out var k) ? k.GetString() : "";

            sb.Append("  - line ").Append(line).Append(", col ").Append(col)
              .Append(": private ").Append(kind).Append(" '").Append(name)
              .Append("' — declared, never referenced elsewhere in this file\n");
        }
    }
}
