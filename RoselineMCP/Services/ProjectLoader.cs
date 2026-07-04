using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Services;

/// <summary>
/// Default <see cref="IProjectLoader"/>. Resolves a project reference to a <c>.csproj</c> or
/// <c>.sln</c> path — accepting a project name, a directory, a <c>.csproj</c> path, a <c>.sln</c>
/// path, or nothing at all (auto-discovery from the working directory) — then opens the containing
/// solution when one can be found (so cross-project references, callers, and renames are complete)
/// or the single project otherwise. This is the single local-path resolution used by the
/// navigation, edit, and diagnostics/fix tools alike; Git-URL loading is intentionally out of
/// scope here — only <c>AnalyzeSolution</c> accepts Git URLs, and it handles them itself.
/// </summary>
public class ProjectLoader : IProjectLoader
{
    /// <summary>How many parent directories to walk when auto-discovering a solution/project.</summary>
    private const int AutoDiscoveryParentDepth = 3;

    private readonly ILogger<ProjectLoader> _logger;
    private readonly IMSBuildService _msBuildService;

    /// <summary>Initializes a new instance of the <see cref="ProjectLoader"/>.</summary>
    public ProjectLoader(ILogger<ProjectLoader> logger, IMSBuildService msBuildService)
    {
        _logger = logger;
        _msBuildService = msBuildService;
    }

    /// <inheritdoc/>
    public async Task<LoadedProject> LoadAsync(string? project, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Resolve the caller's reference (or nothing) to a concrete .sln or .csproj path on disk.
        var targetPath = ResolveTargetPath(project, Directory.GetCurrentDirectory());
        var workspace = _msBuildService.CreateWorkspace();

        try
        {
            Project? primary;

            if (targetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Loading solution for navigation: {Path}", targetPath);
                var solution = await workspace.OpenSolutionAsync(targetPath, cancellationToken: cancellationToken);
                primary = SelectPrimaryProject(solution, targetPath)
                    ?? throw new FileNotFoundException($"No C# project found in solution: {targetPath}");
            }
            else
            {
                primary = null;
                var solutionPath = FindSolutionFile(targetPath);
                if (solutionPath != null)
                {
                    _logger.LogInformation("Loading solution for navigation: {Path}", solutionPath);
                    var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
                    primary = FindProjectInSolution(solution, targetPath, project);
                }

                if (primary == null)
                {
                    _logger.LogInformation("Loading project for navigation: {Path}", targetPath);
                    primary = await workspace.OpenProjectAsync(targetPath, cancellationToken: cancellationToken);
                }
            }

            return new LoadedProject(workspace, primary.Solution, primary);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Chooses the anchor project for a loaded solution: the C# project whose file name (without
    /// extension) matches the <c>.sln</c> name if present, otherwise the first C# project. Symbol
    /// resolution and search still span the whole loaded solution, so this is just a stable anchor.
    /// </summary>
    private static Project? SelectPrimaryProject(Solution solution, string solutionPath)
    {
        var csharpProjects = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        if (csharpProjects.Count == 0)
        {
            return null;
        }

        var solutionName = Path.GetFileNameWithoutExtension(solutionPath);
        var byName = csharpProjects.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p.FilePath ?? p.Name)
                .Equals(solutionName, StringComparison.OrdinalIgnoreCase));

        return byName ?? csharpProjects[0];
    }

    private static Project? FindProjectInSolution(Solution solution, string projectPath, string? projectName)
    {
        var byPath = solution.Projects.FirstOrDefault(p =>
            p.FilePath != null && PathsEqual(p.FilePath, projectPath));
        if (byPath != null)
        {
            return byPath;
        }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            return null;
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
    /// Resolves <paramref name="project"/> to a concrete <c>.sln</c> or <c>.csproj</c> path:
    /// <list type="bullet">
    /// <item>null/whitespace → auto-discover a single solution/project starting from
    /// <paramref name="baseDirectory"/>.</item>
    /// <item>an existing <c>.sln</c> path → that solution.</item>
    /// <item>a <c>.csproj</c> path, a directory containing one, or a bare project name → that
    /// project (existing behavior).</item>
    /// </list>
    /// Throws <see cref="ArgumentException"/> when auto-discovery finds no or multiple candidates, and
    /// <see cref="FileNotFoundException"/> when an explicit reference cannot be resolved.
    /// Internal (rather than private) so <see cref="CachingProjectLoader"/> can compute the same
    /// resolved path as a cache key — keeping <c>null</c>/name/directory aliases of the same target
    /// on a single cache entry.
    /// </summary>
    internal static string ResolveTargetPath(string? project, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return AutoDiscover(baseDirectory);
        }

        if (project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && File.Exists(project))
        {
            return Path.GetFullPath(project);
        }

        return ResolveProjectPath(project);
    }

    /// <summary>
    /// Auto-discovers a single solution/project near <paramref name="baseDirectory"/>: prefers a
    /// solution, then a project, searching the directory itself, up to
    /// <see cref="AutoDiscoveryParentDepth"/> parent directories, and immediate subdirectories.
    /// Throws <see cref="ArgumentException"/> with an actionable message when zero or multiple
    /// candidates are found.
    /// </summary>
    private static string AutoDiscover(string baseDirectory)
    {
        var searchDirectories = DiscoveryDirectories(baseDirectory);

        var solutions = FindFilesAcross(searchDirectories, "*.sln");
        if (solutions.Count == 1)
        {
            return solutions[0];
        }

        if (solutions.Count > 1)
        {
            throw new ArgumentException(BuildAmbiguityMessage("solution (.sln)", solutions));
        }

        var projects = FindFilesAcross(searchDirectories, "*.csproj");
        if (projects.Count == 1)
        {
            return projects[0];
        }

        if (projects.Count > 1)
        {
            throw new ArgumentException(BuildAmbiguityMessage("project (.csproj)", projects));
        }

        throw new ArgumentException(
            $"Could not auto-discover a C# solution or project from '{baseDirectory}' " +
            "(searched the working directory, up to 3 parent directories, and immediate subdirectories). " +
            "Pass an explicit 'project' — a project name, a directory, or a path to a .csproj or .sln file.");
    }

    /// <summary>
    /// The set of directories auto-discovery inspects, in priority order: the base directory, up to
    /// <see cref="AutoDiscoveryParentDepth"/> parents, then the base directory's immediate
    /// subdirectories. Duplicates and non-existent directories are skipped.
    /// </summary>
    private static List<string> DiscoveryDirectories(string baseDirectory)
    {
        var directories = new List<string>();

        void Add(string? directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            var full = Path.GetFullPath(directory);
            if (!directories.Any(d => PathsEqual(d, full)))
            {
                directories.Add(full);
            }
        }

        Add(baseDirectory);

        var parent = Directory.Exists(baseDirectory) ? Directory.GetParent(baseDirectory) : null;
        for (var i = 0; i < AutoDiscoveryParentDepth && parent != null; i++)
        {
            Add(parent.FullName);
            parent = parent.Parent;
        }

        if (Directory.Exists(baseDirectory))
        {
            foreach (var subdirectory in Directory.GetDirectories(baseDirectory))
            {
                Add(subdirectory);
            }
        }

        return directories;
    }

    /// <summary>Collects distinct files matching <paramref name="pattern"/> across <paramref name="directories"/> (top level of each).</summary>
    private static List<string> FindFilesAcross(IEnumerable<string> directories, string pattern)
    {
        var found = new List<string>();
        foreach (var directory in directories)
        {
            foreach (var file in Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                var full = Path.GetFullPath(file);
                if (!found.Any(f => PathsEqual(f, full)))
                {
                    found.Add(full);
                }
            }
        }

        return found;
    }

    private static string BuildAmbiguityMessage(string kind, IReadOnlyList<string> candidates)
    {
        var list = string.Join(", ", candidates.Select(c => $"'{c}'"));
        return $"Found multiple candidate {kind} files near the working directory: {list}. " +
            "Pass an explicit 'project' — a project name, a directory, or a path to a .csproj or .sln file — to disambiguate.";
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
