using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MyAiGen;

/// <summary>
/// Tracks which (path, line-range) spans have already been read or written during a
/// session, and gives a hard yes/no on whether a new read_file call is necessary.
///
/// Your current AgenticWorkflow relies entirely on prompt text ("HARD CONSTRAINT",
/// "BANNED", worked examples...) to stop the model re-reading files. That only works
/// if the model perfectly remembers and obeys ~2000 lines of instructions for the
/// whole conversation — it won't, especially once the transcript is long. This class
/// makes the constraint real: ExecuteToolCall consults it and can refuse the call
/// before it ever touches disk or spends context on file content the model already has.
///
/// One instance per AgenticSession (it's conversation-scoped, not process-scoped —
/// a new chat should start with a clean ledger).
/// </summary>
public sealed class ReadLedger
{
    private sealed class Coverage
    {
        // Merged inclusive line ranges already delivered to the model for this path.
        public List<(int Start, int End)> Ranges = new();
        public DateTime LastWriteUtcAtRead; // file's on-disk mtime at the time we read it
    }

    private readonly Dictionary<string, Coverage> _coverage = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    // Files the model has successfully written this session (full content authored by
    // the model itself). The overwrite gate in WriteFile consults this alongside
    // coverage: a file the model wrote has no unseen disk content that an overwrite
    // could silently drop, so re-writing it must not require a fresh full re-read —
    // especially since RecordWrite removes coverage (to allow the write→verify re-read
    // pattern), which otherwise makes every second write to the same file hit the gate.
    // Cleared with the rest of the ledger on Clear(): after compaction or a new prompt
    // the model's context no longer holds the content it authored, so the gate should
    // require a re-read again instead of trusting stale memory.
    private readonly HashSet<string> _writtenPaths = new(StringComparer.OrdinalIgnoreCase);
    // Counts consecutive full-read-block hits for a path since the last write.
    // Lets callers escalate the nudge if the model keeps trying anyway instead
    // of calling write_file or moving on.
    private readonly Dictionary<string, int> _fullReadBlockCount = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Call when HasFullCoverage blocks a full re-read. Returns the number
    /// of consecutive times this path has been blocked since its last write (1 = first block).</summary>
    public int RegisterFullReadBlock(string relativePath)
    {
        lock (_lock)
        {
            _fullReadBlockCount.TryGetValue(relativePath, out var count);
            count++;
            _fullReadBlockCount[relativePath] = count;
            return count;
        }
    }

    /// <summary>Returns true if at least one read has been recorded for this path in this session.</summary>
    public bool HasCoverage(string relativePath)
    {
        lock (_lock)
        {
            return _coverage.ContainsKey(relativePath);
        }
    }

    /// <summary>Returns true if the recorded ranges cover every line of the file (1 to totalLines).
    /// Used by Gate 1 to distinguish "fully read" from "partially read" — a partial read should
    /// NOT block the model from requesting the remaining lines.</summary>
    public bool HasFullCoverage(string relativePath, int totalLines)
    {
        lock (_lock)
        {
            if (!_coverage.TryGetValue(relativePath, out var cov)) return false;
            return IsFullyCovered(cov.Ranges, 1, totalLines);
        }
    }
    private readonly Dictionary<string, int> _rangeBlockCount = new(StringComparer.OrdinalIgnoreCase);

    public int RegisterRangeBlock(string relativePath, int startLine, int endLine)
    {
        lock (_lock)
        {
            var key = $"{relativePath}|{startLine}-{endLine}";
            _rangeBlockCount.TryGetValue(key, out var count);
            count++;
            _rangeBlockCount[key] = count;
            return count;
        }
    }
    /// <summary>
    /// Wipes all tracked coverage. Call this whenever the conversation content the
    /// ledger is tracking gets discarded or replaced out from under it — the two known
    /// cases are (1) a new user message starts historyForApi over from scratch (no
    /// prior read_file results carry forward), and (2) mid-turn compaction replaces
    /// historyForApi with a summary. In both cases the model's actual context no
    /// longer contains the raw file content the ledger recorded, so "already covered"
    /// would be actively wrong, not just conservative.
    /// Written-path tracking is wiped here too: the model's context no longer holds
    /// the content it authored either, so the overwrite gate should require a fresh
    /// re-read instead of trusting stale memory.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _coverage.Clear();
            _fullReadBlockCount.Clear();
            _rangeBlockCount.Clear();
            _writtenPaths.Clear();
        }
    }
    public enum Verdict { Allow, AlreadyCovered }

    public sealed class Decision
    {
        public Verdict Verdict;
        public string? Reason;
        public List<(int Start, int End)> MissingChunks = new();

        /// <summary>
        /// The file's on-disk mtime as observed at the moment this Decision was made
        /// (null if the file didn't exist or couldn't be stat'd). Callers should pass
        /// this into RecordRead so the ledger records the file state it actually
        /// reasoned about, instead of re-stat'ing after reading — re-stat'ing leaves a
        /// window where a concurrent write between CheckRead and RecordRead would get
        /// silently recorded as "covered" even though the content that was read is
        /// already stale.
        /// </summary>
        public DateTime? ObservedMtimeUtc;

        public static Decision Allow(DateTime? mtime = null) => new() { Verdict = Verdict.Allow, ObservedMtimeUtc = mtime };
        public static Decision Blocked(string reason, DateTime? mtime = null) => new() { Verdict = Verdict.AlreadyCovered, Reason = reason, ObservedMtimeUtc = mtime };
        public static Decision Partial(List<(int Start, int End)> missing, string reason, DateTime? mtime = null) => new() { Verdict = Verdict.AlreadyCovered, Reason = reason, MissingChunks = missing, ObservedMtimeUtc = mtime };
    }

    public Decision CheckRead(string relativePath, int startLine, int endLine, string fullPathOnDisk)
    {
        lock (_lock)
        {
            // Capture the mtime once, up front, so every exit path below (including
            // the early "no prior coverage" Allow) reports a consistent snapshot that
            // the caller can hand back to RecordRead. Previously this was only read
            // inside the "cov exists" branch, so the fast-path Allow() carried no
            // mtime at all and RecordRead had to re-stat, reopening the TOCTOU gap
            // this class exists to close.
            DateTime? observedMtime = null;
            if (File.Exists(fullPathOnDisk))
            {
                try { observedMtime = File.GetLastWriteTimeUtc(fullPathOnDisk); } catch { /* treat as unknown */ }
            }

            if (!_coverage.TryGetValue(relativePath, out var cov))
                return Decision.Allow(observedMtime);

            if (observedMtime.HasValue && observedMtime.Value > cov.LastWriteUtcAtRead)
            {
                _coverage.Remove(relativePath);
                return Decision.Allow(observedMtime);
            }

            if (endLine == int.MaxValue)
            {
                var resolved = TryResolveEndLine(fullPathOnDisk);
                if (resolved == null)
                {
                    // Couldn't determine the file's real length (missing/locked/unreadable).
                    // Fail OPEN rather than proceed with int.MaxValue in the range math below,
                    // which would corrupt missing-chunk calculations. Forcing a real read here
                    // is always safe; incorrectly blocking one would not be.
                    return Decision.Allow(observedMtime);
                }
                endLine = resolved.Value;
            }

            // Calculate the exact lines the agent is MISSING
            var missingChunks = new List<(int Start, int End)>();
            int current = startLine;
            var sortedRanges = cov.Ranges.OrderBy(r => r.Start).ToList();
            foreach (var r in sortedRanges)
            {
                if (r.Start > current)
                    missingChunks.Add((current, Math.Min(r.Start - 1, endLine)));
                current = Math.Max(current, r.End + 1);
                if (current > endLine) break;
            }
            if (current <= endLine)
                missingChunks.Add((current, endLine));

            // If there are no missing chunks, it's 100% redundant. Hard block.
            if (missingChunks.Count == 0)
            {
                var rangesDesc = string.Join(", ", cov.Ranges.Select(r => $"{r.Start}-{r.End}"));
                return Decision.Blocked(
                    $"REDUNDANT_READ_BLOCKED: '{relativePath}' lines {startLine}-{endLine} are 100% redundant. " +
                    $"You already have these exact lines (previously read: {rangesDesc}). " +
                    "Do NOT read this range again. Use write_file to make changes.",
                    observedMtime);
            }

            // Check if there is ANY overlap
            bool hasOverlap = cov.Ranges.Any(r => r.Start <= endLine && r.End >= startLine);

            if (hasOverlap)
            {
                // Partial overlap! Return the missing chunks so MainWindow can auto-read them.
                // Note: a model that dodges the 100%-redundant block above by nudging its
                // start/end by a few lines each call (window-shifting) will keep sailing
                // through here, since each request genuinely does contain a sliver of new
                // content. This class intentionally does NOT try to detect that pattern —
                // it's a stalling behavior, not a duplicate-content problem, and stalling can
                // show up via any tool (read_file, search_files, run_command...), not just
                // this one. That's handled once, generically, by the tool-agnostic stall
                // counter in MainWindow_xaml.cs (callsSinceLastWrite) instead of being
                // re-implemented per-tool here.
                return Decision.Partial(missingChunks,
                    $"PARTIAL_OVERLAP: '{relativePath}' lines {startLine}-{endLine} overlap with lines you already read. " +
                    $"Here are ONLY the missing lines you requested:",
                    observedMtime);
            }

            return Decision.Allow(observedMtime);
        }
    }

    /// <param name="knownMtimeUtc">
    /// Optional mtime already observed for this file (e.g. Decision.ObservedMtimeUtc from
    /// the CheckRead call that authorized this read). When supplied, it is used verbatim
    /// instead of re-stat'ing the file after the read completed — re-stat'ing after the
    /// fact would happily record a write that raced in between CheckRead and RecordRead
    /// as if it had been part of the content the model actually received. When omitted,
    /// falls back to stat'ing fresh (e.g. for callers that don't have a Decision handy).
    /// </param>
    public void RecordRead(string relativePath, int startLine, int endLine, string fullPathOnDisk, DateTime? knownMtimeUtc = null)
    {
        lock (_lock)
        {
            // Caller (AgenticWorkflow.ReadFile) always passes an already-NormalizeRel'd
            // key — do not re-normalize here with a different implementation.
            if (!_coverage.TryGetValue(relativePath, out var cov))
            {
                cov = new Coverage();
                _coverage[relativePath] = cov;
            }

            // If endLine was omitted (int.MaxValue), resolve it to the actual file length
            // so it doesn't break the range math elsewhere. Uses the same resolution logic
            // as CheckRead (previously this used File.ReadAllLines while CheckRead counted
            // newlines via StreamReader — those two can disagree by one on files without a
            // trailing newline, silently desyncing what "already covered" means between the
            // two methods).
            if (endLine == int.MaxValue)
            {
                var resolved = TryResolveEndLine(fullPathOnDisk);
                if (resolved != null) endLine = resolved.Value;
                // If resolution fails, leave endLine as-is; the recorded range will simply
                // undercount as "not fully covering" the file, which is the safe direction
                // to be wrong in (it can only cause an extra read later, never a false block).
            }

            cov.Ranges.Add((startLine, endLine));
            cov.Ranges = MergeRanges(cov.Ranges);

            if (knownMtimeUtc.HasValue)
            {
                cov.LastWriteUtcAtRead = knownMtimeUtc.Value;
            }
            else if (File.Exists(fullPathOnDisk))
            {
                try { cov.LastWriteUtcAtRead = File.GetLastWriteTimeUtc(fullPathOnDisk); }
                catch { /* leave previous value — better than throwing out of a record call */ }
            }
        }
    }

    /// <summary>Call AFTER a write_file call succeeds with non-empty content, so the next
    /// read_file on this path is allowed through (the content genuinely changed).</summary>
    public void RecordWrite(string relativePath)
    {
        lock (_lock)
        {
            _coverage.Remove(relativePath);
            _fullReadBlockCount.Remove(relativePath);
            _rangeBlockCount.Keys.Where(k => k.StartsWith(relativePath + "|", StringComparison.OrdinalIgnoreCase))
                .ToList().ForEach(k => _rangeBlockCount.Remove(k));
        }
    }

    /// <summary>True if this session's model has successfully written this path — the
    /// model authored the file's full current content itself, so an overwrite cannot
    /// drop content it never saw.</summary>
    public bool HasWritten(string relativePath)
    {
        lock (_lock)
        {
            return _writtenPaths.Contains(relativePath);
        }
    }

    /// <summary>Marks a path as authored by the model (call after a successful
    /// write_file). Deliberately separate from RecordWrite: delete/move/rename also
    /// call RecordWrite to clear coverage, but must NOT make a path writable — moving
    /// a file the model never read doesn't give it knowledge of that file's content,
    /// and the overwrite gate must keep blocking those.</summary>
    public void MarkAsWritten(string relativePath)
    {
        lock (_lock)
        {
            _writtenPaths.Add(relativePath);
        }
    }

    /// <summary>
    /// Call when a model has been blocked repeatedly on redundant reads of a path and
    /// the caller wants to give it one more chance next turn WITHOUT pretending the file
    /// changed. Unlike RecordWrite, this does NOT clear _coverage — the model still has
    /// to actually cover new ground (or the range/full-read block will simply re-trigger
    /// once it re-requests the same lines). It only clears the escalation counters so a
    /// stuck model isn't permanently deadlocked on this path. Using RecordWrite for this
    /// purpose was a bug: it wiped recorded coverage for a file nothing had touched on
    /// disk, letting the model freely re-read the untouched content and restart the same
    /// redundant-read cycle from zero — the exact loop this class exists to stop.
    /// </summary>
    public void ResetBlockCounters(string relativePath)
    {
        lock (_lock)
        {
            _fullReadBlockCount.Remove(relativePath);
            _rangeBlockCount.Keys.Where(k => k.StartsWith(relativePath + "|", StringComparison.OrdinalIgnoreCase))
                .ToList().ForEach(k => _rangeBlockCount.Remove(k));
        }
    }

    /// <summary>Human-readable dump for debugging / optionally surfacing to the model
    /// as a compact status line instead of re-deriving it from transcript scanning.</summary>
    public string DescribeCoverage()
    {
        lock (_lock)
        {
            if (_coverage.Count == 0) return "(no files read yet)";
            return string.Join("\n", _coverage.Select(kv =>
                $"{kv.Key}: {string.Join(",", kv.Value.Ranges.Select(r => $"{r.Start}-{r.End}"))}"));
        }
    }

    /// <summary>
    /// Single source of truth for "how many lines does this file have", used by both
    /// CheckRead and RecordRead so they can never disagree on what int.MaxValue resolves
    /// to. Returns null (never throws) if the file is missing, locked, or unreadable —
    /// callers must decide how to fail (this class always fails open, see call sites).
    /// </summary>
    private static int? TryResolveEndLine(string fullPathOnDisk)
    {
        try
        {
            if (!File.Exists(fullPathOnDisk)) return null;
            return File.ReadAllLines(fullPathOnDisk).Length;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFullyCovered(List<(int Start, int End)> ranges, int start, int end) =>
        ranges.Any(r => r.Start <= start && r.End >= end);

    private static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<(int Start, int End)>();
        foreach (var r in sorted)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End + 1)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.End, r.End));
            }
            else
            {
                merged.Add(r);
            }
        }
        return merged;
    }
}