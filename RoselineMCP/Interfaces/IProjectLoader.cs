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
    /// identically: same project name, same solution-root-relative file paths. Reports the value
    /// the loader supplied when it knows which file answered; otherwise falls back to
    /// <c>Solution.FilePath ?? Project.FilePath ?? string.Empty</c> — the only signal available for
    /// handles built without that knowledge (e.g. in-memory <c>AdhocWorkspace</c> handles in tests).
    /// </summary>
    public string ResolvedPath => _resolvedPath ?? Solution.FilePath ?? Project.FilePath ?? string.Empty;

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
    public LoadedProject(
        Workspace workspace, Solution solution, Project project, bool ownsWorkspace = true, string? resolvedPath = null)
    {
        Workspace = workspace;
        Solution = solution;
        Project = project;
        _ownsWorkspace = ownsWorkspace;
        _resolvedPath = resolvedPath;
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
