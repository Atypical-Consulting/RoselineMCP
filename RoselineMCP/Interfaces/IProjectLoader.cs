using Microsoft.CodeAnalysis;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Loads a C# project (by name, directory, <c>.csproj</c> path, or <c>.sln</c> path — or nothing,
/// auto-discovering from the working directory) into a fresh <see cref="Workspace"/> for
/// structural/semantic queries and edits. When the project belongs to a solution, the whole
/// solution is opened so cross-project references, callers, and renames resolve correctly;
/// otherwise the single project is opened on its own.
/// </summary>
public interface IProjectLoader
{
    /// <summary>
    /// Resolves and loads <paramref name="project"/>, returning a disposable handle. Always dispose
    /// the handle: it releases the underlying <see cref="Workspace"/> when it owns it (the default
    /// loader), and is a safe no-op for the shared workspaces handed out by the caching loader.
    /// </summary>
    /// <param name="project">
    /// Project name, directory containing a single <c>.csproj</c>, a path to a <c>.csproj</c> file,
    /// or a path to a <c>.sln</c> file. When <see langword="null"/> or whitespace, the solution/project
    /// is auto-discovered nearest-first: the working directory itself wins when it has exactly one
    /// candidate, then each parent directory (up to a few levels) in order, then immediate
    /// subdirectories; only a level with multiple candidates of its own — or no match anywhere —
    /// throws <see cref="System.ArgumentException"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the load.</param>
    /// <returns>A handle exposing the loaded <see cref="Solution"/> and the primary <see cref="Project"/>.</returns>
    Task<LoadedProject> LoadAsync(string? project, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the solution (or lone project) that <b>owns a given file</b>, anchoring resolution on
    /// that file's absolute path rather than on the process working directory.
    /// </summary>
    /// <param name="absoluteFilePath">
    /// Absolute path of a file on disk. The nearest <c>.csproj</c> at or above it is located, and
    /// the containing solution is preferred exactly as in <see cref="LoadAsync"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the load.</param>
    /// <returns>
    /// A handle for the owning solution/project, or <see langword="null"/> when the file belongs to
    /// no project at all. "Nothing to load" is an ordinary answer here, not a failure: the compile
    /// guard fires after every write, including writes nowhere near a C# solution.
    /// </returns>
    /// <remarks>
    /// This exists because the server's working directory is fixed at spawn and is <em>not</em> the
    /// agent's — they diverge whenever work happens in a git worktree, and two checkouts of the same
    /// repository are otherwise reported identically. Anchoring on the edited file is what keeps the
    /// guard's verdict about the tree the agent actually wrote to.
    /// </remarks>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="absoluteFilePath"/> is blank or not rooted.
    /// </exception>
    Task<LoadedProject?> LoadForFileAsync(string absoluteFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Disposable result of <see cref="IProjectLoader.LoadAsync"/>. By default the handle owns the
/// underlying <see cref="Workspace"/> and disposing the handle disposes the workspace; a caching
/// loader hands out non-owning handles (<c>ownsWorkspace: false</c>) so disposing them leaves the
/// shared, cached workspace alive. The workspace is typed as the <see cref="Workspace"/> base class
/// (rather than <c>MSBuildWorkspace</c>) so tests can supply an in-memory <c>AdhocWorkspace</c>
/// without pulling in MSBuild.
/// </summary>
public sealed class LoadedProject : IDisposable
{
    private readonly bool _ownsWorkspace;
    private readonly string? _resolvedPath;

    /// <summary>The workspace the project/solution was loaded into.</summary>
    public Workspace Workspace { get; }

    /// <summary>The loaded solution (a single-project solution when no <c>.sln</c> was found).</summary>
    public Solution Solution { get; }

    /// <summary>The primary project that was requested — the anchor for symbol resolution.</summary>
    public Project Project { get; }

    /// <summary>
    /// The absolute path that was actually resolved and loaded — the file the loader opened to
    /// answer the call. This is the only thing that distinguishes two checkouts of the same
    /// repository (a git worktree from its main checkout), which are otherwise reported
    /// identically: same project name, same relative file paths. Reports the value
    /// the loader supplied when it knows which file answered; otherwise falls back to
    /// <c>Solution.FilePath ?? Project.FilePath ?? string.Empty</c> — the only signal available for
    /// handles built without that knowledge (e.g. in-memory <c>AdhocWorkspace</c> handles in tests).
    /// </summary>
    public string ResolvedPath => _resolvedPath ?? Solution.FilePath ?? Project.FilePath ?? string.Empty;

    /// <summary>
    /// The directory every relative file path in a response hangs off — always the directory
    /// containing <see cref="ResolvedPath"/>, so a caller can combine
    /// <c>Path.GetDirectoryName(resolvedPath)</c> with any returned path and land on the real file.
    /// That is the whole promise of <see cref="ResolvedPath"/>, and it only holds while the two are
    /// computed from the same value: the navigation tools, <c>ApplyFixes</c>, the edit tools and the
    /// verification path each derived this directory from <c>Solution.FilePath ?? Project.FilePath</c>
    /// instead, which diverges from <see cref="ResolvedPath"/> for a <c>.csproj</c> not listed in its
    /// nearest ancestor <c>.sln</c> — Roslyn grafts such a project onto the already-open solution, so
    /// <c>Solution.FilePath</c> keeps naming the <c>.sln</c> that never contributed it (issues #181
    /// and #199; this is now the only place that expression survives).
    /// <see langword="null"/> when there is no path to anchor on at all (an in-memory workspace),
    /// which leaves paths absolute — the same fallback those call sites had before.
    /// </summary>
    public string? BaseDirectory => Path.GetDirectoryName(ResolvedPath);

    /// <summary>
    /// The absolute <c>.sln</c> or <c>.csproj</c> the caller's <c>project</c> reference resolved
    /// to <em>before</em> the loader opened anything — what the caller named, as opposed to
    /// <see cref="ResolvedPath"/>, which is what answered. The two differ for a <c>.csproj</c>
    /// listed in an ancestor <c>.sln</c>: the loader opens the solution (so <see cref="ResolvedPath"/>
    /// is the <c>.sln</c>) but the caller chose one project, and a tool that reports "the other
    /// projects were skipped" must not say so to a caller who asked for exactly that. Falls back to
    /// <see cref="ResolvedPath"/> for handles built without that knowledge.
    /// </summary>
    public string TargetPath => _targetPath ?? ResolvedPath;

    private readonly string? _targetPath;

    /// <summary>Initializes a new <see cref="LoadedProject"/>.</summary>
    /// <param name="workspace">The workspace the project/solution was loaded into.</param>
    /// <param name="solution">The loaded solution snapshot.</param>
    /// <param name="project">The primary project within <paramref name="solution"/>.</param>
    /// <param name="ownsWorkspace">
    /// Whether disposing this handle disposes <paramref name="workspace"/>. Defaults to
    /// <see langword="true"/> (the pre-caching behavior); the workspace cache passes
    /// <see langword="false"/> so shared workspaces survive handle disposal.
    /// </param>
    /// <param name="resolvedPath">
    /// The file the loader actually opened, when the caller knows it precisely — e.g. a
    /// <c>.csproj</c> that was opened standalone because it isn't listed in its nearest ancestor
    /// <c>.sln</c>, a case <see cref="ResolvedPath"/>'s fallback expression cannot distinguish from
    /// "the solution answered" once Roslyn has grafted the project onto it. <see langword="null"/>
    /// (the default) defers to that fallback.
    /// </param>
    /// <param name="targetPath">
    /// The <c>.sln</c> or <c>.csproj</c> the caller's reference resolved to, when the loader knows
    /// it — see <see cref="TargetPath"/>. <see langword="null"/> (the default) defers to
    /// <see cref="ResolvedPath"/>.
    /// </param>
    public LoadedProject(
        Workspace workspace, Solution solution, Project project, bool ownsWorkspace = true, string? resolvedPath = null, string? targetPath = null)
    {
        Workspace = workspace;
        Solution = solution;
        Project = project;
        _ownsWorkspace = ownsWorkspace;
        _resolvedPath = resolvedPath;
        _targetPath = targetPath;
    }

    /// <summary>Disposes the underlying workspace when this handle owns it; otherwise a no-op.</summary>
    public void Dispose()
    {
        if (_ownsWorkspace)
        {
            Workspace.Dispose();
        }
    }
}
