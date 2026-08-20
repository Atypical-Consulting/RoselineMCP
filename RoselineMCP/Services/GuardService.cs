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
/// <b>Structural changes reset the baseline.</b> Adding or removing a file changes the project graph,
/// which <see cref="Solution.WithDocumentText(DocumentId, SourceText, PreservationMode)"/> cannot
/// express. Rather than diff against a snapshot that no longer describes the tree, the entry is
/// dropped and re-established, and that pass stays silent.
/// </para>
/// </remarks>
public sealed class GuardService : IGuardService, IDisposable
{
    /// <summary>Maximum diagnostics carried in one verdict; the rest are counted in <c>omitted</c>.</summary>
    private const int MaxDiagnostics = 20;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IProjectLoader _projectLoader;
    private readonly IVerificationService _verificationService;
    private readonly ILogger<GuardService> _logger;

    private readonly Lock _sync = new();
    private readonly Dictionary<string, Entry> _entries;
    private readonly Dictionary<string, Task<GuardReport>> _inFlight;

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

        // Key the in-flight map on the resolved .csproj rather than the loaded solution: it is
        // available without loading anything, which is the point — a second write arriving mid-verify
        // must join the first rather than start a second compile of the same solution.
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
            _entries.TryGetValue(projectPath, out entry);
        }

        if (entry is null)
        {
            return await EstablishBaselineAsync(absoluteFilePath, projectPath, cancellationToken);
        }

        var candidate = TryAdvance(entry, out var changedAnything);
        if (candidate is null)
        {
            // Structure moved under us (a file appeared or vanished). Re-establish rather than diff
            // against a snapshot that no longer describes the tree.
            lock (_sync)
            {
                _entries.Remove(projectPath);
            }

            _logger.LogDebug("Guard baseline reset for {Target}: the document set changed on disk", projectPath);
            return await EstablishBaselineAsync(absoluteFilePath, projectPath, cancellationToken);
        }

        if (!changedAnything)
        {
            // Nothing on disk differs from what the guard already verified.
            return GuardReport.Quiet(entry.ResolvedPath);
        }

        var verdict = await _verificationService.VerifyAsync(entry.Snapshot, candidate, MaxDiagnostics, cancellationToken);

        lock (_sync)
        {
            entry.Snapshot = candidate;
            entry.Stamps = StampAll(candidate);
            _entries[projectPath] = entry;
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

        var entry = new Entry
        {
            Snapshot = loaded.Solution,
            Stamps = StampAll(loaded.Solution),
            ResolvedPath = loaded.ResolvedPath,
        };

        lock (_sync)
        {
            _entries[projectPath] = entry;
        }

        _logger.LogDebug("Guard baseline established for {Target}", entry.ResolvedPath);

        // First sighting: there is no before-state, so there is no delta to report. Saying "this
        // solution has 40 errors" here would be blaming the agent for the branch it landed on.
        return GuardReport.Quiet(entry.ResolvedPath);
    }

    /// <summary>
    /// Rebuilds the snapshot from disk, returning the new solution — or <see langword="null"/> when
    /// the document set itself changed and a text-only advance cannot express it.
    /// </summary>
    private static Solution? TryAdvance(Entry entry, out bool changedAnything)
    {
        changedAnything = false;
        var solution = entry.Snapshot;

        foreach (var project in entry.Snapshot.Projects)
        {
            foreach (var document in project.Documents)
            {
                var path = document.FilePath;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var stamp = Stamp(path);
                if (stamp is null)
                {
                    // A tracked document vanished: structural, not textual.
                    return null;
                }

                if (entry.Stamps.TryGetValue(path, out var previous) && previous == stamp.Value)
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (IOException)
                {
                    // Mid-write, or locked. Treat as "nothing to say" rather than guessing.
                    return null;
                }
                catch (UnauthorizedAccessException)
                {
                    return null;
                }

                solution = solution.WithDocumentText(document.Id, SourceText.From(text));
                changedAnything = true;
            }
        }

        return solution;
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

                var stamp = Stamp(path);
                if (stamp is not null)
                {
                    stamps[path] = stamp.Value;
                }
            }
        }

        return stamps;
    }

    private static (long Ticks, long Length)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Releases the snapshots this service holds.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_sync)
        {
            _entries.Clear();
            _inFlight.Clear();
        }
    }

    private sealed class Entry
    {
        public required Solution Snapshot { get; set; }

        public required Dictionary<string, (long Ticks, long Length)> Stamps { get; set; }

        public required string ResolvedPath { get; init; }
    }
}
