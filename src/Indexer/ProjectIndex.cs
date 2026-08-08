using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MyAiGen;

/// <summary>
/// One entry per indexed file. Cheap to hold thousands of these in memory —
/// we never store full file content, only metadata + extracted symbol names.
/// </summary>
public sealed class IndexedFile
{
    public string FullPath = "";
    public string RelativePath = "";
    public string Extension = "";
    public long SizeBytes;
    public int LineCount;
    public DateTime LastWriteUtc;

    /// <summary>Method/class/function names extracted with a cheap regex pass.</summary>
    public List<string> Symbols = new();

    /// <summary>Bulleted, human-readable summary of what the file does.</summary>
    public string Summary = "";

    /// <summary>Lower-cased tokens (filename parts + symbols + summary words) used for search.</summary>
    public HashSet<string> Tokens = new();
}

public sealed class SearchHit
{
    public string RelativePath = "";
    public double Score;
    public List<string> MatchedSymbols = new();
    public string Summary = "";
}

/// <summary>
/// Builds a lightweight in-memory index of a project (like a poor-man's version of
/// what Cursor/opencode do offline): filenames, symbol names, and a one-line summary
/// per file, plus an inverted token index for fast ranked search.
/// </summary>
public sealed class ProjectIndex
{
    private readonly string _projectPath;
    private readonly ConcurrentDictionary<string, IndexedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _invertedIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _buildLock = new();
    private DateTime _lastFullBuildUtc = DateTime.MinValue;
    private static readonly TimeSpan FullRescanInterval = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
        { "obj", "bin", "node_modules", ".git", "venv", "__pycache__", "target", "build", "dist", ".next" };

    public ProjectIndex(string projectPath)
    {
        _projectPath = projectPath;
    }

    public int FileCount => _files.Count;
    public bool IsBuilt => _lastFullBuildUtc != DateTime.MinValue;

    public void Build()
    {
        if (!Directory.Exists(_projectPath)) return;
        lock (_buildLock)
        {
            _files.Clear();
            _invertedIndex.Clear();

            // One batch tree-sitter pass across every supported-language file in the
            // project (single process spawn) instead of re-deriving symbols per file
            // via regex — see TreeSitterProjectAnalyzer. Null means unavailable/failed;
            // IndexOneFile falls back to the regex path per file when that happens.
            var tsIndex = TreeSitterProjectAnalyzer.IndexProject(_projectPath);

            var files = Directory.EnumerateFiles(_projectPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !PathHasIgnoredDir(f));

            foreach (var f in files)
            {
                try { IndexOneFile(f, tsIndex); }
                catch { /* unreadable/locked file — skip, don't fail the whole build */ }
            }
            _lastFullBuildUtc = DateTime.UtcNow;
        }
    }

    public void EnsureFresh()
    {
        if (!IsBuilt || DateTime.UtcNow - _lastFullBuildUtc > FullRescanInterval)
            Build();
    }

    public void Invalidate(string fullPath)
    {
        var rel = ToRelative(fullPath);
        if (_files.TryRemove(rel, out var old))
            RemoveFromInvertedIndex(rel, old.Tokens);

        if (File.Exists(fullPath) && !PathHasIgnoredDir(fullPath))
        {
            // For a tree-sitter-supported file, re-run the (single-spawn, whole-project)
            // tree-sitter index so the file just written gets the same precise
            // Symbols/Summary as a full Build() would give it, instead of silently
            // falling back to the weaker regex extraction until the next periodic
            // rescan. Only pay for the extra spawn when it actually matters — files
            // in languages tree-sitter doesn't cover skip straight to IndexOneFile's
            // own regex fallback, same as before.
            Dictionary<string, List<TsSymbol>>? tsIndex = null;
            var ext = Path.GetExtension(fullPath);
            if (TreeSitterProjectAnalyzer.SupportedExtensions.Contains(ext))
                tsIndex = TreeSitterProjectAnalyzer.IndexProject(_projectPath);

            try { IndexOneFile(fullPath, tsIndex); } catch { }
        }
    }

    public List<SearchHit> Search(string query, int topN = 8)
    {
        EnsureFresh();
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return new List<SearchHit>();

        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var qt in queryTokens)
        {
            if (_invertedIndex.TryGetValue(qt, out var exactPaths))
                foreach (var p in exactPaths)
                    scores[p] = scores.GetValueOrDefault(p) + 3.0;

            foreach (var kv in _invertedIndex)
            {
                if (kv.Key.Equals(qt, StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Key.Contains(qt, StringComparison.OrdinalIgnoreCase))
                    foreach (var p in kv.Value)
                        scores[p] = scores.GetValueOrDefault(p) + 1.0;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv =>
            {
                var f = _files[kv.Key];
                return new SearchHit
                {
                    RelativePath = f.RelativePath,
                    Score = kv.Value,
                    Summary = f.Summary,
                    MatchedSymbols = f.Symbols
                        .Where(s => queryTokens.Any(qt => s.Contains(qt, StringComparison.OrdinalIgnoreCase)))
                        .Take(5).ToList()
                };
            })
            .ToList();
    }

    public IReadOnlyCollection<IndexedFile> AllFiles() { EnsureFresh(); return _files.Values.ToList(); }

    // ---- internals ----

    private void IndexOneFile(string fullPath, Dictionary<string, List<TsSymbol>>? tsIndex = null)
    {
        var info = new FileInfo(fullPath);
        var rel = ToRelative(fullPath);
        var ext = info.Extension;

        var entry = new IndexedFile
        {
            FullPath = fullPath,
            RelativePath = rel,
            Extension = ext,
            SizeBytes = info.Length,
            LastWriteUtc = info.LastWriteTimeUtc,
        };

        var looksTextual = info.Length < 512 * 1024 && IsLikelyTextExtension(ext);
        if (looksTextual)
        {
            var text = File.ReadAllText(fullPath);
            entry.LineCount = text.Count(c => c == '\n') + 1;

            List<TsSymbol>? tsSymbols = null;
            if (TreeSitterProjectAnalyzer.SupportedExtensions.Contains(ext) && tsIndex != null)
                tsIndex.TryGetValue(rel.Replace('\\', '/'), out tsSymbols);

            if (tsSymbols != null)
            {
                entry.Summary = BuildSummaryFromSymbols(text, tsSymbols);
                entry.Symbols = tsSymbols.Select(s => s.Name).Distinct().Take(200).ToList();
            }
            else
            {
                entry.Summary = ExtractSummary(text, ext);
                if (SymbolPatterns.TryGetValue(ext, out var pattern))
                    entry.Symbols = pattern.Matches(text)
                        .Select(m => m.Groups["n"].Success ? m.Groups["n"].Value : "")
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct()
                        .Take(200)
                        .ToList();
            }
        }

        foreach (var t in Tokenize(Path.GetFileNameWithoutExtension(rel)))
            entry.Tokens.Add(t);
        foreach (var s in entry.Symbols)
            foreach (var t in Tokenize(s))
                entry.Tokens.Add(t);
        foreach (var t in Tokenize(entry.Summary))
            entry.Tokens.Add(t);

        _files[rel] = entry;
        AddToInvertedIndex(rel, entry.Tokens);
    }

    private void AddToInvertedIndex(string rel, HashSet<string> tokens)
    {
        foreach (var t in tokens)
            _invertedIndex.AddOrUpdate(t,
                _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rel },
                (_, set) => { lock (set) { set.Add(rel); } return set; });
    }

    private void RemoveFromInvertedIndex(string rel, HashSet<string> tokens)
    {
        foreach (var t in tokens)
            if (_invertedIndex.TryGetValue(t, out var set))
                lock (set) { set.Remove(rel); }
    }

    /// <summary>
    /// Extracts a readable, bulleted summary of what the file does by parsing
    /// XML doc comments, class/struct declarations, and method names.
    /// Converts PascalCase/camelCase to "Pascal Case" for readability.
    /// </summary>
    private static string ExtractSummary(string text, string ext)
    {
        var bullets = new List<string>();
        var lines = text.Split('\n');

        // 1. Try to find standard <summary> tags
        var xmlSummary = ExtractXmlDocSummary(lines);
        if (xmlSummary.Length > 0) bullets.Add(xmlSummary);

        // 2. Extract class/interface/struct/enum declarations
        string mainType = "";
        foreach (var line in lines.Take(150))
        {
            var match = _typePattern.Match(line);
            if (!match.Success) match = _pyClassPattern.Match(line);
            if (!match.Success) match = _jsClassPattern.Match(line);

            if (match.Success)
            {
                mainType = match.Value.Trim();
                // Keep the human-written doc summary first when it exists — it's far more
                // useful to the agent than the bare type signature. Only lead with mainType
                // when there's no xmlSummary to show.
                bullets.Add(mainType);
                break;
            }
        }

        // 3. Extract method names and convert to readable bullet points
        var methods = new HashSet<string>();
        foreach (var line in lines.Take(300))
        {
            var match = _methodPattern.Match(line);
            if (!match.Success) match = _pyDefPattern.Match(line);
            if (match.Success)
            {
                var name = match.Groups["name"].Value;
                if (name != "if" && name != "for" && name != "while" && name != "switch" && name != "catch" && name != "using" && name != "lock")
                {
                    methods.Add(name);
                }
            }
        }

        foreach (var m in methods.Take(8))
        {
            var readableName = Regex.Replace(m, @"([a-z])([A-Z])", "$1 $2");
            bullets.Add($"- {readableName}");
        }

        if (bullets.Count == 0)
        {
            foreach (var raw in lines.Take(30))
            {
                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("using ") || line.StartsWith("namespace ") || line.StartsWith("import ")) continue;
                if (line.StartsWith("#", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("[", StringComparison.OrdinalIgnoreCase) && line.EndsWith("]")) continue;

                var cleanLine = line.TrimStart('/', '*', ' ');
                if (cleanLine.Length > 3)
                {
                    return cleanLine.Length > 140 ? cleanLine[..140] : cleanLine;
                }
            }
        }

        return string.Join("\n", bullets);
    }

    private static string ExtractXmlDocSummary(string[] lines)
    {
        bool inSummary = false;
        var summaryBuilder = new StringBuilder();
        foreach (var raw in lines.Take(100))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("/// <summary>", StringComparison.OrdinalIgnoreCase)) { inSummary = true; continue; }
            if (trimmed.StartsWith("/// </summary>", StringComparison.OrdinalIgnoreCase)) break;
            if (inSummary)
            {
                var content = trimmed.TrimStart('/').Trim();
                if (!string.IsNullOrWhiteSpace(content)) summaryBuilder.Append(content + " ");
            }
        }
        return summaryBuilder.ToString().Trim();
    }

    /// <summary>
    /// Same bulleted-summary shape as ExtractSummary, but sourced from a precise
    /// tree-sitter parse instead of regex — used for supported-language files when
    /// the batch TreeSitterProjectAnalyzer.IndexProject() pass succeeded. XML
    /// doc-comment scanning is unchanged (tree-sitter symbols don't carry
    /// doc-comment prose, so that part of the pipeline is shared with the regex
    /// path).
    /// </summary>
    private static string BuildSummaryFromSymbols(string text, List<TsSymbol> symbols)
    {
        var bullets = new List<string>();
        var lines = text.Split('\n');

        var xmlSummary = ExtractXmlDocSummary(lines);
        if (xmlSummary.Length > 0) bullets.Add(xmlSummary);

        var mainType = symbols.FirstOrDefault(s =>
            s.Kind is TsSymbolKind.Class or TsSymbolKind.Interface or TsSymbolKind.Struct
                or TsSymbolKind.Enum or TsSymbolKind.Record);
        if (mainType != null)
        {
            var kindWord = mainType.Kind.ToString().ToLowerInvariant();
            var line = string.IsNullOrEmpty(mainType.Accessibility)
                ? $"{kindWord} {mainType.Name}"
                : $"{mainType.Accessibility} {kindWord} {mainType.Name}";
            // Same fix as ExtractSummary: don't bump the real doc summary out of first
            // place. Only lead with the type line when there's no xmlSummary.
            bullets.Add(line);
        }

        foreach (var m in symbols.Where(s =>
                 s.Kind is TsSymbolKind.Method or TsSymbolKind.Constructor or TsSymbolKind.Function
                     or TsSymbolKind.Event or TsSymbolKind.Operator or TsSymbolKind.Indexer
                     or TsSymbolKind.Destructor)
                 .Select(s => s.Name).Distinct().Take(8))
        {
            var readableName = Regex.Replace(m, @"([a-z])([A-Z])", "$1 $2");
            bullets.Add($"- {readableName}");
        }

        return string.Join("\n", bullets);
    }

    private static readonly Dictionary<string, Regex> SymbolPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = new Regex(@"(?:class|interface|record|struct|enum)\s+(?<n>\w+)|(?:public|private|protected|internal|static|async)\s+[\w<>\[\],\s]+?\s+(?<n>\w+)\s*\(", RegexOptions.Compiled),
        [".py"] = new Regex(@"^\s*(?:class|def)\s+(?<n>\w+)", RegexOptions.Compiled | RegexOptions.Multiline),
        [".ts"] = new Regex(@"(?:class|interface|function|const)\s+(?<n>\w+)", RegexOptions.Compiled),
        [".js"] = new Regex(@"(?:class|function|const)\s+(?<n>\w+)", RegexOptions.Compiled),
    };
    private static readonly Regex _typePattern = new(@"^\s*(?:public|internal|private|protected)?\s*(?:static|abstract|sealed|partial|async)?\s*(class|interface|struct|enum|record)\s+(?<name>\w+)", RegexOptions.Compiled);
    private static readonly Regex _pyClassPattern = new(@"^\s*class\s+(?<name>\w+)", RegexOptions.Compiled);
    private static readonly Regex _jsClassPattern = new(@"^\s*(?:export\s+)?(?:default\s+)?(?:class|function|const)\s+(?<name>\w+)", RegexOptions.Compiled);
    private static readonly Regex _methodPattern = new(@"^\s*(?:public|private|protected|internal)?\s*(?:static|virtual|override|async|abstract)?\s*[\w<>\[\],\s]+?\s+(?<name>\w+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex _pyDefPattern = new(@"^\s*def\s+(?<name>\w+)\s*\(", RegexOptions.Compiled);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".py", ".ts", ".tsx", ".js", ".jsx", ".json", ".md", ".xaml", ".yaml", ".yml",
          ".txt", ".html", ".css", ".xml", ".csproj", ".sln", ".config" };
    private static bool IsLikelyTextExtension(string ext) => TextExtensions.Contains(ext);

    private static bool PathHasIgnoredDir(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => IgnoredDirs.Contains(part));

    private string ToRelative(string fullPath) => AgenticWorkflow.NormalizeRel(fullPath, _projectPath);
    private static readonly Regex TokenSplit = new(@"[^a-zA-Z0-9]+|(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);

    private static List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return TokenSplit.Split(text)
            .Where(t => t.Length > 1)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();
    }
}