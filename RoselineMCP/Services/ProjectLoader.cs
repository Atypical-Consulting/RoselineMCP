using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// Default <see cref="IProjectLoader"/>. Resolves a project reference to a <c>.csproj</c> path,
/// then opens the containing solution when one can be found (so cross-project references, callers,
/// and renames are complete) or the single project otherwise. Mirrors the local-path resolution
/// used by the diagnostics tools; Git-URL loading is intentionally out of scope here — navigation
/// and edits operate on a local working copy.
/// </summary>
public class ProjectLoader : IProjectLoader
{
    private readonly ILogger<ProjectLoader> _logger;
    private readonly IMSBuildService _msBuildService;

    /// <summary>Initializes a new instance of the <see cref="ProjectLoader"/>.</summary>
    public ProjectLoader(ILogger<ProjectLoader> logger, IMSBuildService msBuildService)
    {
        _logger = logger;
        _msBuildService = msBuildService;
    }

    /// <inheritdoc/>
    public async Task<LoadedProject> LoadAsync(string project, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var projectPath = ResolveProjectPath(project);
        var workspace = _msBuildService.CreateWorkspace();

        try
        {
            var solutionPath = FindSolutionFile(projectPath);
            Project? primary = null;

            if (solutionPath != null)
            {
                _logger.LogInformation("Loading solution for navigation: {Path}", solutionPath);
                var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
                primary = FindProjectInSolution(solution, projectPath, project);
            }

            if (primary == null)
            {
                _logger.LogInformation("Loading project for navigation: {Path}", projectPath);
                primary = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
            }

            return new LoadedProject(workspace, primary.Solution, primary);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static Project? FindProjectInSolution(Solution solution, string projectPath, string projectName)
    {
        var byPath = solution.Projects.FirstOrDefault(p =>
            p.FilePath != null && PathsEqual(p.FilePath, projectPath));
        if (byPath != null)
        {
            return byPath;
        }

        return solution.Projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a),
                Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Resolves <paramref name="project"/> (a <c>.csproj</c> path, a directory, or a bare project
    /// name) to a concrete <c>.csproj</c> path, throwing <see cref="FileNotFoundException"/> if none
    /// can be found so the tool layer reports a <c>NotFoundError</c>.
    /// </summary>
    private static string ResolveProjectPath(string project)
    {
        if (File.Exists(project) && project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return project;
        }

        if (Directory.Exists(project))
        {
            var csprojFiles = Directory.GetFiles(project, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
            {
                return csprojFiles[0];
            }
        }

        var currentDirFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj", SearchOption.AllDirectories);
        var match = currentDirFiles.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f).Equals(project, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        throw new FileNotFoundException($"Project not found: {project}");
    }

    private static string? FindSolutionFile(string startPath)
    {
        var directory = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);

        while (!string.IsNullOrEmpty(directory))
        {
            var slnFiles = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
            {
                return slnFiles[0];
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }
}
