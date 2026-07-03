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
    /// Resolves and loads <paramref name="project"/>, returning a disposable handle that owns the
    /// underlying <see cref="Workspace"/>. Dispose it to release the workspace.
    /// </summary>
    /// <param name="project">
    /// Project name, directory containing a single <c>.csproj</c>, a path to a <c>.csproj</c> file,
    /// or a path to a <c>.sln</c> file. When <see langword="null"/> or whitespace, the solution/project
    /// is auto-discovered from the working directory (up to a few parent directories and immediate
    /// subdirectories); an ambiguous or empty search throws <see cref="System.ArgumentException"/>.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the load.</param>
    /// <returns>A handle exposing the loaded <see cref="Solution"/> and the primary <see cref="Project"/>.</returns>
    Task<LoadedProject> LoadAsync(string? project, CancellationToken cancellationToken = default);
}

/// <summary>
/// Disposable result of <see cref="IProjectLoader.LoadAsync"/>. Owns the underlying
/// <see cref="Workspace"/>; disposing the handle disposes the workspace. The workspace is typed as
/// the <see cref="Workspace"/> base class (rather than <c>MSBuildWorkspace</c>) so tests can supply an
/// in-memory <c>AdhocWorkspace</c> without pulling in MSBuild.
/// </summary>
public sealed class LoadedProject : IDisposable
{
    /// <summary>The workspace the project/solution was loaded into.</summary>
    public Workspace Workspace { get; }

    /// <summary>The loaded solution (a single-project solution when no <c>.sln</c> was found).</summary>
    public Solution Solution { get; }

    /// <summary>The primary project that was requested — the anchor for symbol resolution.</summary>
    public Project Project { get; }

    /// <summary>Initializes a new <see cref="LoadedProject"/>.</summary>
    public LoadedProject(Workspace workspace, Solution solution, Project project)
    {
        Workspace = workspace;
        Solution = solution;
        Project = project;
    }

    /// <summary>Disposes the underlying workspace.</summary>
    public void Dispose() => Workspace.Dispose();
}
