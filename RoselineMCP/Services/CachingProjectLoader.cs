using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// <see cref="IProjectLoader"/> decorator that caches the loaded workspace across tool calls.
/// Loading a solution through MSBuild dominates every navigation/edit tool's latency (hundreds of
/// milliseconds per call, all reload); Roslyn <see cref="Solution"/> snapshots are immutable, so
/// reusing the loaded workspace is safe as long as the cache is invalidated when anything relevant
/// changes on disk.
///
/// Entries are keyed by the resolved target path (the <c>.sln</c>/<c>.csproj</c> the inner loader
/// would open), so <c>null</c>, project-name, directory, and file-path aliases of the same target
/// all hit one entry. Each entry carries a fingerprint — last-write-time + length of the
/// <c>.sln</c>, every <c>.csproj</c>, and every document in the solution, plus the last-write-time
/// of the directories containing them (which catches files being added or removed) — that is
/// re-checked with cheap <c>stat</c> calls on every load; any mismatch disposes the cached
/// workspace and reloads fresh. This also self-invalidates after RoselineMCP's own
/// <c>ApplyFixes</c>/<c>EditMember</c>/<c>RenameSymbol</c> disk writes.
///
/// The cache is bounded (<see cref="MaxEntries"/> entries, least-recently-used eviction, evicted
/// workspaces disposed) and guarded by a <see cref="SemaphoreSlim"/>. Handles returned from the
/// cache do not own the shared workspace (<c>ownsWorkspace: false</c>), so disposing them — as all
/// call sites already do — is a no-op. Disable via <c>RoselineMCP:WorkspaceCache = false</c>
/// (<see cref="RoselineMcpOptions.WorkspaceCache"/>) to pass every call through to the inner
/// loader unchanged.
/// </summary>
public sealed class CachingProjectLoader : IProjectLoader, IDisposable
{
    /// <summary>Maximum number of cached workspaces before the least-recently-used one is evicted.</summary>
    internal const int MaxEntries = 4;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IProjectLoader _inner;
    private readonly ILogger<CachingProjectLoader> _logger;
    private readonly bool _enabled;
    private readonly Func<string?, string> _resolveCacheKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _entries;
    private long _accessCounter;
    private bool _disposed;

    /// <summary>Initializes a new <see cref="CachingProjectLoader"/> wrapping <paramref name="inner"/>.</summary>
    /// <param name="inner">The real loader used on cache misses (typically <see cref="ProjectLoader"/>).</param>
    /// <param name="options">Options carrying the <see cref="RoselineMcpOptions.WorkspaceCache"/> switch.</param>
    /// <param name="logger">Logger for cache hit/invalidation/eviction diagnostics (stderr).</param>
    /// <param name="resolveCacheKey">
    /// Test seam: maps the raw <c>project</c> argument to the cache key. Defaults to the inner
    /// loader's own resolution (<see cref="ProjectLoader.ResolveTargetPath"/> against the current
    /// working directory).
    /// </param>
    public CachingProjectLoader(
        IProjectLoader inner,
        IOptions<RoselineMcpOptions> options,
        ILogger<CachingProjectLoader> logger,
        Func<string?, string>? resolveCacheKey = null)
    {
        _inner = inner;
        _logger = logger;
        _enabled = options.Value.WorkspaceCache;
        _resolveCacheKey = resolveCacheKey
            ?? (project => ProjectLoader.ResolveTargetPath(project, Directory.GetCurrentDirectory()));
        _entries = new Dictionary<string, CacheEntry>(PathComparer);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Resolves the owning <c>.csproj</c> and then goes through this decorator's own
    /// <see cref="LoadAsync"/>, so a file-anchored load is a cache <em>hit</em> on the same entry a
    /// path-anchored one would use. Delegating to the inner loader instead would bypass the cache
    /// and pay a full MSBuild reload on every write — which, for a guard that fires after every
    /// write, is the difference between sub-second and unusable.
    /// </remarks>
    public async Task<LoadedProject?> LoadForFileAsync(string absoluteFilePath, CancellationToken cancellationToken = default)
    {
        var projectPath = ProjectLoader.ResolveProjectForFile(absoluteFilePath);

        return projectPath is null ? null : await LoadAsync(projectPath, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<LoadedProject> LoadAsync(string? project, CancellationToken cancellationToken = default)
    {
        if (!_enabled)
        {
            return await _inner.LoadAsync(project, cancellationToken);
        }

        string key;
        try
        {
            key = Path.GetFullPath(_resolveCacheKey(project));
        }
        catch
        {
            // Resolution failed (not found / ambiguous / permission denied — a named directory the
            // server cannot read throws from here too). Delegate uncached so the inner loader
            // surfaces its own, canonical error for the tool layer to classify. The permission case
            // pays for resolution twice, which is accepted: it is a failure path, and re-resolving
            // is what keeps this class from owning a second copy of the error contract.
            return await _inner.LoadAsync(project, cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.Fingerprint.IsCurrent())
                {
                    entry.LastAccess = ++_accessCounter;
                    _logger.LogDebug("Workspace cache hit for {Target}", key);
                    return new LoadedProject(entry.Workspace, entry.Solution, entry.Project, ownsWorkspace: false);
                }

                _logger.LogInformation("Workspace cache invalidated for {Target} — files changed on disk; reloading", key);
                _entries.Remove(key);
                entry.Workspace.Dispose();
            }

            var loaded = await _inner.LoadAsync(project, cancellationToken);
            var fingerprint = WorkspaceFingerprint.Capture(key, loaded.Solution);

            EvictLeastRecentlyUsedIfFull();
            _entries[key] = new CacheEntry(loaded.Workspace, loaded.Solution, loaded.Project, fingerprint)
            {
                LastAccess = ++_accessCounter,
            };

            // The cache now owns the workspace; hand the caller a non-owning handle so its
            // (existing) `using` disposal doesn't tear down the shared workspace.
            return new LoadedProject(loaded.Workspace, loaded.Solution, loaded.Project, ownsWorkspace: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Evicts (and disposes) least-recently-used entries until an insert fits the bound.</summary>
    private void EvictLeastRecentlyUsedIfFull()
    {
        while (_entries.Count >= MaxEntries)
        {
            var (lruKey, lruEntry) = _entries.MinBy(kv => kv.Value.LastAccess);
            _entries.Remove(lruKey);
            lruEntry.Workspace.Dispose();
            _logger.LogInformation("Workspace cache evicted least-recently-used entry {Target}", lruKey);
        }
    }

    /// <summary>Disposes every cached workspace. Called once at host shutdown.</summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                entry.Workspace.Dispose();
            }

            _entries.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>A live cached workspace plus the disk fingerprint it was loaded from.</summary>
    private sealed class CacheEntry
    {
        public Workspace Workspace { get; }
        public Solution Solution { get; }
        public Project Project { get; }
        public WorkspaceFingerprint Fingerprint { get; }
        public long LastAccess { get; set; }

        public CacheEntry(Workspace workspace, Solution solution, Project project, WorkspaceFingerprint fingerprint)
        {
            Workspace = workspace;
            Solution = solution;
            Project = project;
            Fingerprint = fingerprint;
        }
    }

    /// <summary>
    /// Disk fingerprint of a loaded solution: a stamp (exists + last-write-time UTC + length) for
    /// the resolved target, the <c>.sln</c>, every <c>.csproj</c>, and every document file, plus a
    /// stamp for each distinct directory containing them (a directory's last-write-time changes when
    /// a direct child is added, removed, or renamed — catching new files that per-file stamps
    /// cannot). Re-checking is a handful of <c>stat</c> calls, no MSBuild involved.
    /// </summary>
    internal sealed class WorkspaceFingerprint
    {
        private readonly List<FileStamp> _files;
        private readonly List<DirectoryStamp> _directories;

        private WorkspaceFingerprint(List<FileStamp> files, List<DirectoryStamp> directories)
        {
            _files = files;
            _directories = directories;
        }

        /// <summary>Captures the fingerprint for <paramref name="solution"/> as currently on disk.</summary>
        public static WorkspaceFingerprint Capture(string targetPath, Solution solution)
        {
            var paths = new HashSet<string>(PathComparer) { targetPath };

            if (!string.IsNullOrEmpty(solution.FilePath))
            {
                paths.Add(Path.GetFullPath(solution.FilePath));
            }

            foreach (var project in solution.Projects)
            {
                if (!string.IsNullOrEmpty(project.FilePath))
                {
                    paths.Add(Path.GetFullPath(project.FilePath));
                }

                foreach (var document in project.Documents)
                {
                    if (!string.IsNullOrEmpty(document.FilePath))
                    {
                        paths.Add(Path.GetFullPath(document.FilePath));
                    }
                }
            }

            var directories = new HashSet<string>(PathComparer);
            foreach (var path in paths)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    directories.Add(directory);
                }
            }

            return new WorkspaceFingerprint(
                paths.Select(FileStamp.Capture).ToList(),
                directories.Select(DirectoryStamp.Capture).ToList());
        }

        /// <summary>Re-stats every tracked file/directory; <see langword="false"/> means stale.</summary>
        public bool IsCurrent()
        {
            foreach (var stamp in _files)
            {
                if (FileStamp.Capture(stamp.Path) != stamp)
                {
                    return false;
                }
            }

            foreach (var stamp in _directories)
            {
                if (DirectoryStamp.Capture(stamp.Path) != stamp)
                {
                    return false;
                }
            }

            return true;
        }

        private readonly record struct FileStamp(string Path, bool Exists, DateTime LastWriteTimeUtc, long Length)
        {
            public static FileStamp Capture(string path)
            {
                var info = new FileInfo(path);
                return info.Exists
                    ? new FileStamp(path, true, info.LastWriteTimeUtc, info.Length)
                    : new FileStamp(path, false, default, 0);
            }
        }

        private readonly record struct DirectoryStamp(string Path, bool Exists, DateTime LastWriteTimeUtc)
        {
            public static DirectoryStamp Capture(string path)
            {
                var info = new DirectoryInfo(path);
                return info.Exists
                    ? new DirectoryStamp(path, true, info.LastWriteTimeUtc)
                    : new DirectoryStamp(path, false, default);
            }
        }
    }
}
