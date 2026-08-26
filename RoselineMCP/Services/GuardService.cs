using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Default <see cref="IGuardService"/>: keeps a Roslyn <see cref="Solution"/> snapshot per resolved
/// path and edits it <b>forward</b> from disk on every write, so the compiler's verdict is a delta
/// against what the guard last saw rather than an absolute statement about the branch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a snapshot and not a reload.</b> <c>VerificationService</c> derives its scope from
/// <c>candidate.GetChanges(baseline)</c>, which is only meaningful across solutions that share a
/// lineage. Two independent loads of the same solution mint fresh <see cref="ProjectId"/>s and
/// <see cref="DocumentId"/>s, so nothing matches and every pre-existing error is reported as
/// introduced — measured at <c>introduced: 1, preexisting: 0</c> on two loads of identical broken
/// code. Editing one snapshot forward keeps the lineage intact, which also keeps the compiled scope
/// down to the changed project and its dependents: that is what makes the guard fast enough to run
/// after every single write.
/// </para>
/// <para>
/// <b>Why the mtime resync.</b> A snapshot only stays true while every write goes through the guard.
/// A <c>git checkout</c>, a rebase, or a tool the hook does not cover changes files behind its back,
/// and a stale snapshot would then blame the agent for a difference it never made. So each pass
/// re-reads every tracked document whose size or last-write-time moved, not merely the file that was
/// just written.
/// </para>
/// <para>
/// <b>Stamps are captured when a file is read, never afterwards.</b> Verification takes seconds; a
/// write landing inside that window must not be recorded as already-seen. Re-stat'ing after the
/// compile would pair a file's <em>new</em> mtime with its <em>old</em> text in the snapshot, and
/// that edit would then be skipped forever.
/// </para>
/// <para>
/// <b>Structural changes reset the baseline.</b> Adding or removing a file changes the project graph,
/// which <see cref="Solution.WithDocumentText(DocumentId, SourceText, PreservationMode)"/> cannot
/// express. Rather than diff against a snapshot that no longer describes the tree, the entry is
/// dropped and re-established, and that pass stays silent. A file that merely could not be read
/// right now — locked, mid-write, permission-denied — is <em>not</em> structural and keeps its
/// baseline: throwing a good baseline away over a transient lock would cost a full reload and say
/// nothing.
/// </para>
/// </remarks>
public sealed class GuardService : IGuardService, IDisposable
{
    /// <summary>Maximum diagnostics carried in one verdict; the rest are counted in <c>omitted</c>.</summary>
    private const int MaxDiagnostics = 20;

    /// <summary>
    /// Maximum solutions kept under guard before the least-recently-used one is dropped. Mirrors
    /// <see cref="CachingProjectLoader.MaxEntries"/> on purpose: each entry pins a whole
    /// <see cref="Solution"/> graph, and an unbounded map would quietly defeat that ceiling.
    /// </summary>
    internal const int MaxEntries = 4;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IProjectLoader _projectLoader;
    private readonly IVerificationService _verificationService;
    private readonly ILogger<GuardService> _logger;

    private readonly Lock _sync = new();

    /// <summary>Keyed on the resolved <c>.sln</c>/<c>.csproj</c>, never on the anchor project.</summary>
    /// <remarks>
    /// One solution is one entry. Keying on the nearest <c>.csproj</c> instead would give a
    /// multi-project solution one full-solution snapshot per project touched — N× the memory, and
    /// worse, each entry's resync would pick up the others' edits and report the same error again
    /// under whichever file was written last.
    /// </remarks>
    private readonly Dictionary<string, Entry> _entries;

    /// <summary>Anchor <c>.csproj</c> → resolved solution path, so a lookup needs no load.</summary>
    private readonly Dictionary<string, string> _resolvedByProject;

    private readonly Dictionary<string, Task<GuardReport>> _inFlight;

    private long _accessCounter;
    private bool _disposed;

    /// <summary>Initializes a new <see cref="GuardService"/>.</summary>
    public GuardService(
        IProjectLoader projectLoader,
        IVerificationService verificationService,
        ILogger<GuardService> logger)
    {
        _projectLoader = projectLoader;
        _verificationService = verificationService;
        _logger = logger;
        _entries = new Dictionary<string, Entry>(PathComparer);
        _resolvedByProject = new Dictionary<string, string>(PathComparer);
        _inFlight = new Dictionary<string, Task<GuardReport>>(PathComparer);
    }

    /// <inheritdoc/>
    public async Task<GuardReport> VerifyFileAsync(string absoluteFilePath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var projectPath = ProjectLoader.ResolveProjectForFile(absoluteFilePath);
        if (projectPath is null)
        {
            // A write nowhere near a C# project. Ordinary, and nothing to say.
            return GuardReport.Quiet();
        }

        // Dedup on the anchor .csproj rather than the solution: it is available without loading
        // anything, which is the point — a second write arriving mid-verify must join the first
        // rather than start a second compile of the same solution.
        Task<GuardReport> work;
        var owner = false;

        lock (_sync)
        {
            if (!_inFlight.TryGetValue(projectPath, out work!))
            {
                work = RunAsync(absoluteFilePath, projectPath, cancellationToken);
                _inFlight[projectPath] = work;
                owner = true;
            }
        }

        try
        {
            return await work;
        }
        finally
        {
            if (owner)
            {
                lock (_sync)
                {
                    if (_inFlight.TryGetValue(projectPath, out var tracked) && ReferenceEquals(tracked, work))
                    {
                        _inFlight.Remove(projectPath);
                    }
                }
            }
        }
    }

    private async Task<GuardReport> RunAsync(string absoluteFilePath, string projectPath, CancellationToken cancellationToken)
    {
        Entry? entry;
        lock (_sync)
        {
            entry = LookupNoLock(projectPath);
        }

        if (entry is null)
        {
            return await EstablishBaselineAsync(absoluteFilePath, projectPath, cancellationToken);
        }

        var advance = TryAdvance(entry);

        switch (advance.Outcome)
        {
            case AdvanceOutcome.Structural:
                // A tracked document vanished: the project graph moved, which a text-only advance
                // cannot express. Re-establish rather than diff against a snapshot that no longer
                // describes the tree.
                lock (_sync)
                {
                    Forget(entry.ResolvedPath);
                }

                _logger.LogDebug("Guard baseline reset for {Target}: the document set changed on disk", entry.ResolvedPath);
                return await EstablishBaselineAsync(absoluteFilePath, projectPath, cancellationToken);

            case AdvanceOutcome.Unreadable:
                // Locked, mid-write, or permission-denied. Keep the baseline — it is still the best
                // description of the tree we have — and simply say nothing this pass.
                _logger.LogDebug("Guard pass skipped for {Target}: a tracked document could not be read", entry.ResolvedPath);
                return GuardReport.Quiet(entry.ResolvedPath);

            case AdvanceOutcome.Unchanged:
                return GuardReport.Quiet(entry.ResolvedPath);
        }

        var candidate = advance.Candidate!;
        var verdict = await _verificationService.VerifyAsync(
            entry.Snapshot, candidate, entry.BaseDirectory, MaxDiagnostics, cancellationToken);

        lock (_sync)
        {
            if (!_disposed && _entries.TryGetValue(entry.ResolvedPath, out var current) && ReferenceEquals(current, entry))
            {
                entry.Snapshot = candidate;

                // Captured while the files were read, NOT re-stat'ed here: a write that landed during
                // the verification above must keep an old stamp so the next pass still reads it.
                entry.Stamps = advance.Stamps!;
                entry.LastAccess = ++_accessCounter;
            }
        }

        var text = GuardReportFormatter.Format(verdict);
        return text is null
            ? GuardReport.Quiet(entry.ResolvedPath)
            : GuardReport.Speaking(verdict, text, entry.ResolvedPath);
    }

    private async Task<GuardReport> EstablishBaselineAsync(string absoluteFilePath, string projectPath, CancellationToken cancellationToken)
    {
        using var loaded = await _projectLoader.LoadForFileAsync(absoluteFilePath, cancellationToken);
        if (loaded is null)
        {
            return GuardReport.Quiet();
        }

        var resolvedPath = loaded.ResolvedPath;
        var entry = new Entry
        {
            Snapshot = loaded.Solution,
            Stamps = StampAll(loaded.Solution),
            ResolvedPath = resolvedPath,
            BaseDirectory = loaded.BaseDirectory,
        };

        lock (_sync)
        {
            if (_disposed)
            {
                return GuardReport.Quiet(resolvedPath);
            }

            entry.LastAccess = ++_accessCounter;
            _entries[resolvedPath] = entry;
            _resolvedByProject[projectPath] = resolvedPath;
            EvictIfNeededNoLock();
        }

        _logger.LogDebug("Guard baseline established for {Target}", resolvedPath);

        // First sighting: there is no before-state, so there is no delta to report. Saying "this
        // solution has 40 errors" here would be blaming the agent for the branch it landed on.
        return GuardReport.Quiet(resolvedPath);
    }

    private Entry? LookupNoLock(string projectPath)
    {
        if (!_resolvedByProject.TryGetValue(projectPath, out var resolvedPath)
            || !_entries.TryGetValue(resolvedPath, out var entry))
        {
            return null;
        }

        entry.LastAccess = ++_accessCounter;
        return entry;
    }

    private void Forget(string resolvedPath)
    {
        _entries.Remove(resolvedPath);

        foreach (var projectPath in _resolvedByProject
                     .Where(pair => PathComparer.Equals(pair.Value, resolvedPath))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _resolvedByProject.Remove(projectPath);
        }
    }

    private void EvictIfNeededNoLock()
    {
        while (_entries.Count > MaxEntries)
        {
            var oldest = _entries.OrderBy(pair => pair.Value.LastAccess).First().Key;
            _logger.LogDebug("Guard baseline evicted for {Target} (over {Max} solutions)", oldest, MaxEntries);
            Forget(oldest);
        }
    }

    /// <summary>
    /// Rebuilds the snapshot from disk, reporting what kind of change was found and carrying the
    /// stamps observed <em>at read time</em>.
    /// </summary>
    /// <remarks>
    /// <b>Known corner case (#233).</b> A document's stamp is <c>(LastWriteTimeUtc.Ticks, Length)</c>
    /// — cheap, but not a content hash. Two writes whose <c>Length</c> happens to match can produce
    /// an identical stamp if they also land inside the OS's file-timestamp update granularity
    /// (observed on Windows CI runners; NTFS/Win32 file-time updates are commonly ~15.6&#160;ms
    /// ticks, not per-write), and such a write is then silently treated as unchanged — never read,
    /// never verified. This is an accepted trade-off of the fast stat-only path, not something this
    /// method guards against: a full content read/hash on every pass would remove the collision but
    /// also the reason this path exists. In practice a real MCP tool call is separated from the
    /// previous one by an IPC round trip far larger than any OS clock granularity, so the risk is
    /// theoretical for actual agent writes; it is exercised deliberately (and deterministically, via
    /// <c>File.SetLastWriteTimeUtc</c>) by
    /// <c>GuardServiceTests.TryAdvance_Skips_Verification_When_A_Same_Length_Edit_Forces_An_Identical_Stamp</c>.
    /// </remarks>
    private static AdvanceResult TryAdvance(Entry entry)
    {
        var solution = entry.Snapshot;
        var stamps = new Dictionary<string, (long Ticks, long Length)>(PathComparer);
        var changed = false;

        foreach (var project in entry.Snapshot.Projects)
        {
            foreach (var document in project.Documents)
            {
                var path = document.FilePath;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                switch (Stamp(path, out var stamp))
                {
                    case StampOutcome.Missing:
                        return AdvanceResult.Structural;

                    case StampOutcome.Unreadable:
                        return AdvanceResult.Unreadable;
                }

                if (stamps.ContainsKey(path))
                {
                    // A linked file shared by two projects: already read this pass.
                    continue;
                }

                stamps[path] = stamp;

                if (entry.Stamps.TryGetValue(path, out var previous) && previous == stamp)
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return AdvanceResult.Unreadable;
                }

                solution = solution.WithDocumentText(document.Id, SourceText.From(text));
                changed = true;
            }
        }

        return changed
            ? new AdvanceResult(AdvanceOutcome.Advanced, solution, stamps)
            : AdvanceResult.Unchanged;
    }

    private static Dictionary<string, (long Ticks, long Length)> StampAll(Solution solution)
    {
        var stamps = new Dictionary<string, (long, long)>(PathComparer);

        foreach (var project in solution.Projects)
        {
            foreach (var document in project.Documents)
            {
                var path = document.FilePath;
                if (string.IsNullOrEmpty(path) || stamps.ContainsKey(path))
                {
                    continue;
                }

                if (Stamp(path, out var stamp) == StampOutcome.Ok)
                {
                    stamps[path] = stamp;
                }
            }
        }

        return stamps;
    }

    private static StampOutcome Stamp(string path, out (long Ticks, long Length) stamp)
    {
        stamp = default;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return StampOutcome.Missing;
            }

            stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
            return StampOutcome.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // "I could not look" is not "it is gone" — conflating the two throws away a good
            // baseline over a transient lock.
            return StampOutcome.Unreadable;
        }
    }

    /// <summary>Releases the snapshots this service holds.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _disposed = true;
            _entries.Clear();
            _resolvedByProject.Clear();
            _inFlight.Clear();
        }
    }

    private enum StampOutcome
    {
        Ok,
        Missing,
        Unreadable,
    }

    private enum AdvanceOutcome
    {
        Advanced,
        Unchanged,
        Structural,
        Unreadable,
    }

    private sealed record AdvanceResult(
        AdvanceOutcome Outcome,
        Solution? Candidate,
        Dictionary<string, (long Ticks, long Length)>? Stamps)
    {
        public static readonly AdvanceResult Unchanged = new(AdvanceOutcome.Unchanged, null, null);
        public static readonly AdvanceResult Structural = new(AdvanceOutcome.Structural, null, null);
        public static readonly AdvanceResult Unreadable = new(AdvanceOutcome.Unreadable, null, null);
    }

    private sealed class Entry
    {
        public required Solution Snapshot { get; set; }

        public required Dictionary<string, (long Ticks, long Length)> Stamps { get; set; }

        public required string ResolvedPath { get; init; }

        /// <summary>
        /// The anchor the entry's reported file paths hang off — <see cref="LoadedProject.BaseDirectory"/>
        /// of the handle that established this baseline, captured here because the verification runs
        /// long after that handle was disposed. Kept beside <see cref="ResolvedPath"/> so the guard's
        /// paths and the path it reports stay two views of one value (#199).
        /// </summary>
        public required string? BaseDirectory { get; init; }

        public long LastAccess { get; set; }
    }
}
