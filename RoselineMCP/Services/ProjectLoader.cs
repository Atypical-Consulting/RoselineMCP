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
            string resolvedPath;

            if (targetPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Loading solution for navigation: {Path}", targetPath);
                var solution = await workspace.OpenSolutionAsync(targetPath, cancellationToken: cancellationToken);
                primary = SelectPrimaryProject(solution, targetPath)
                    ?? throw new FileNotFoundException($"No C# project found in solution: {targetPath}");
                resolvedPath = targetPath;
            }
            else
            {
                primary = null;
                resolvedPath = targetPath;
                var solutionPath = FindSolutionFile(targetPath);
                if (solutionPath != null)
                {
                    _logger.LogInformation("Loading solution for navigation: {Path}", solutionPath);
                    var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
                    primary = FindProjectInSolution(solution, targetPath, project);
                    if (primary != null)
                    {
                        resolvedPath = solutionPath;
                    }
                }

                if (primary == null)
                {
                    // Not listed in the ancestor .sln (or none exists): opened standalone, grafted
                    // onto whatever workspace.CurrentSolution already is. resolvedPath stays
                    // targetPath — the .csproj that actually answered, not the .sln it was
                    // grafted onto, which LoadedProject.ResolvedPath's own fallback expression
                    // cannot tell apart from "the solution answered" (see #151).
                    _logger.LogInformation("Loading project for navigation: {Path}", targetPath);
                    primary = await workspace.OpenProjectAsync(targetPath, cancellationToken: cancellationToken);
                }
            }

            // ResolveProjectPath's direct-.csproj and directory branches return the caller's
            // argument (or a Directory.GetFiles result derived from it) verbatim, so a relative
            // `project` argument can leave resolvedPath relative too — unlike the pre-#151
            // Solution.FilePath/Project.FilePath fallback, which MSBuildWorkspace always
            // normalizes internally. Normalize here so the documented "absolute path" contract on
            // LoadedProject.ResolvedPath holds regardless of how the caller spelled `project`.
            return new LoadedProject(
                workspace, primary.Solution, primary,
                resolvedPath: Path.GetFullPath(resolvedPath),
                // What the caller named (the .sln, or the .csproj before its ancestor .sln was
                // opened) — distinct from resolvedPath, which is what answered. See LoadedProject.TargetPath.
                targetPath: Path.GetFullPath(targetPath));
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<LoadedProject?> LoadForFileAsync(string absoluteFilePath, CancellationToken cancellationToken = default)
    {
        var projectPath = ResolveProjectForFile(absoluteFilePath);

        return projectPath is null ? null : await LoadAsync(projectPath, cancellationToken);
    }

    /// <summary>
    /// Finds the <c>.csproj</c> nearest to <paramref name="absoluteFilePath"/> by walking upward
    /// from its directory, or <see langword="null"/> when no project sits above it.
    /// </summary>
    /// <remarks>
    /// Nearest wins, so a project nested inside another (a sample app under a library, say) claims
    /// its own files. Finding the containing <c>.sln</c> is deliberately NOT done here — that is
    /// already <see cref="LoadAsync"/>'s job, and duplicating it is how two resolution behaviors
    /// start to drift apart.
    /// </remarks>
    internal static string? ResolveProjectForFile(string absoluteFilePath)
    {
        if (string.IsNullOrWhiteSpace(absoluteFilePath))
        {
            throw new ArgumentException("A file path is required.", nameof(absoluteFilePath));
        }

        if (!Path.IsPathRooted(absoluteFilePath))
        {
            // The caller's working directory is not ours — resolving a relative path here would
            // silently answer about a different tree. See LoadForFileAsync's remarks.
            throw new ArgumentException(
                $"A file-anchored load needs an absolute path; got '{absoluteFilePath}'.",
                nameof(absoluteFilePath));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(absoluteFilePath));

        while (!string.IsNullOrEmpty(directory))
        {
            string[] projects;
            try
            {
                projects = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException)
            {
                // One unreadable directory must not abort the walk: the answer may still be above it,
                // and the guard's contract is to stay silent rather than to fail loudly.
                projects = [];
            }

            if (projects.Length > 0)
            {
                // Sorted so a directory holding several .csproj files resolves deterministically.
                Array.Sort(projects, StringComparer.Ordinal);
                return projects[0];
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
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
    /// <para>
    /// Internal (rather than private) because two callers outside <see cref="ProjectLoader"/> need
    /// the same answer this gives <see cref="LoadAsync"/>:
    /// <see cref="CachingProjectLoader"/> uses it as a cache key, keeping <c>null</c>/name/directory
    /// aliases of one target on a single entry; and the write tools' confirmation gate
    /// (<c>ToolExecutionHelper</c>) uses it to name — and then to write to — the concrete file a
    /// destructive call will modify.
    /// </para>
    /// <para>
    /// ⚠️ That second caller makes the <em>throwing</em> behavior load-bearing, not merely tidy. A
    /// human is asked to approve a write by the path this returns, so it must never soften an
    /// unresolvable or ambiguous reference into a best guess or a placeholder to be more convenient
    /// for a cache: doing so would put a target in front of a human that the write does not use, or
    /// re-introduce the blank prompt that made the confirmation unanswerable. Both exceptions are
    /// relied upon to abort such a call <em>before</em> anyone is asked.
    /// </para>
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

        return ResolveProjectPath(project, baseDirectory);
    }

    /// <summary>
    /// Auto-discovers a single solution/project near <paramref name="baseDirectory"/>, nearest
    /// level first: the base directory itself, then each parent directory (up to
    /// <see cref="AutoDiscoveryParentDepth"/>) in order, then the base directory's immediate
    /// subdirectories as the final level. The first level that yields exactly one candidate wins,
    /// so a solution in the working directory is never made ambiguous by another one further up
    /// the tree (e.g. a git worktree nested inside its main checkout). A level that itself yields
    /// multiple candidates is a genuine ambiguity and fails, listing that level's candidates.
    /// Solutions are preferred over projects: the <c>.csproj</c> fallback (same nearest-level-first
    /// walk) only runs when no level yields a <c>.sln</c> at all. Throws
    /// <see cref="ArgumentException"/> with an actionable message when nothing is found or a level
    /// is ambiguous.
    /// </summary>
    private static string AutoDiscover(string baseDirectory)
    {
        var levels = DiscoveryLevels(baseDirectory);

        var solution = FindNearest(levels, "*.sln", "solution (.sln)");
        if (solution != null)
        {
            return solution;
        }

        var project = FindNearest(levels, "*.csproj", "project (.csproj)");
        if (project != null)
        {
            return project;
        }

        throw new ArgumentException(
            $"Could not auto-discover a C# solution or project from '{baseDirectory}' " +
            "(searched the working directory first, then up to 3 parent directories, then immediate subdirectories). " +
            "Pass an explicit 'project' — a project name, a directory, or a path to a .csproj or .sln file.");
    }

    /// <summary>
    /// Walks <paramref name="levels"/> nearest-first and returns the single file matching
    /// <paramref name="pattern"/> from the first level that has any. Throws
    /// <see cref="ArgumentException"/> when that level itself contains multiple candidates
    /// (a genuine ambiguity — farther levels never contribute to it); returns <c>null</c> when no
    /// level has a match.
    /// </summary>
    private static string? FindNearest(IEnumerable<IReadOnlyList<string>> levels, string pattern, string kind)
    {
        foreach (var level in levels)
        {
            var candidates = FindFilesAcross(level, pattern);
            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            if (candidates.Count > 1)
            {
                throw new ArgumentException(BuildAmbiguityMessage(kind, candidates));
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the enumeration settings shared by every INCIDENTAL directory scan (auto-discovery
    /// and the bare-name sweep). <see cref="EnumerationOptions.IgnoreInaccessible"/> defaults
    /// <c>true</c> here — and covers the enumeration's own root, measured — whereas the
    /// <see cref="SearchOption"/> overloads pass <see cref="EnumerationOptions.Compatible"/>
    /// (<c>false</c>). The other two properties are pinned back to <c>Compatible</c>'s values:
    /// <c>AttributesToSkip = 0</c> because the default (<c>Hidden | System</c>) hides every
    /// dot-directory on Unix, and <see cref="MatchType.Win32"/> for pattern parity. That pin
    /// un-hides dot-<em>files</em> along with the directories — one attribute cannot tell the two
    /// apart — so the shadow files it lets through are filtered by NAME in
    /// <see cref="IncidentalFiles"/>, which every incidental file scan goes through. Scans where the
    /// CALLER NAMED the directory (<see cref="ResolveProjectPath"/>'s first branch) deliberately do
    /// NOT use this. One factory rather than two independently-maintained property lists, so a
    /// property added here can never silently diverge between the non-recursive and recursive scan.
    /// </summary>
    private static EnumerationOptions CreateIncidentalScan(bool recurseSubdirectories = false) => new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        MatchType = MatchType.Win32,
        RecurseSubdirectories = recurseSubdirectories
    };

    /// <summary>Non-recursive incidental scan — <see cref="DiscoveryLevels"/>, <see cref="FindFilesAcross"/>, <see cref="FindSolutionFile"/>.</summary>
    private static readonly EnumerationOptions IncidentalScan = CreateIncidentalScan();

    /// <summary>Recursive incidental scan — the bare-name sweep in <see cref="ResolveProjectPath"/>.</summary>
    private static readonly EnumerationOptions IncidentalRecursiveScan = CreateIncidentalScan(recurseSubdirectories: true);

    /// <summary>
    /// Whether <paramref name="path"/> names an AppleDouble shadow — the <c>._&lt;name&gt;</c> file
    /// macOS writes beside <c>&lt;name&gt;</c> to carry its extended attributes through a filesystem
    /// that has none (exFAT, SMB, some zip tools), and which then travels back with the tree. .NET
    /// infers <c>Hidden</c> from the leading dot on Unix for files exactly as it does for
    /// directories, so the <c>AttributesToSkip = 0</c> pin that keeps dot-directories discoverable
    /// un-hides these too; and on Windows nothing ever hid them by attribute. The name is the only
    /// thing that tells a shadow from a real file on every platform, so the name is what is tested.
    /// </summary>
    private static bool IsAppleDoubleShadow(string path) =>
        Path.GetFileName(path.AsSpan()).StartsWith("._", StringComparison.Ordinal);

    /// <summary>
    /// The one incidental FILE enumeration:
    /// <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/> under
    /// <paramref name="options"/>, minus AppleDouble shadows. Every incidental scan of files goes
    /// through here, so the filter can never apply at one call site and not another — a shadow that
    /// slips through is either a spurious ambiguity (two "solutions" where there is one) or a silent
    /// wrong pick (a resource fork handed to MSBuild), and both were measured before this existed.
    /// </summary>
    private static IEnumerable<string> IncidentalFiles(string directory, string pattern, EnumerationOptions options) =>
        Directory.EnumerateFiles(directory, pattern, options).Where(f => !IsAppleDoubleShadow(f));

    /// <summary>
    /// The levels auto-discovery inspects, nearest first: the base directory; each parent
    /// directory (up to <see cref="AutoDiscoveryParentDepth"/>) as its own level; then the base
    /// directory's immediate subdirectories together as the final level. Non-existent and
    /// unreadable directories are skipped.
    /// </summary>
    private static List<IReadOnlyList<string>> DiscoveryLevels(string baseDirectory)
    {
        var levels = new List<IReadOnlyList<string>>();

        if (!Directory.Exists(baseDirectory))
        {
            return levels;
        }

        levels.Add([Path.GetFullPath(baseDirectory)]);

        var parent = Directory.GetParent(baseDirectory);
        for (var i = 0; i < AutoDiscoveryParentDepth && parent != null; i++)
        {
            levels.Add([parent.FullName]);
            parent = parent.Parent;
        }

        var subdirectories = Directory.GetDirectories(baseDirectory, "*", IncidentalScan);
        if (subdirectories.Length > 0)
        {
            levels.Add(subdirectories.Select(Path.GetFullPath).ToList());
        }

        return levels;
    }

    /// <summary>
    /// Collects distinct files matching <paramref name="pattern"/> across <paramref name="directories"/>
    /// (top level of each), AppleDouble shadows excluded. A directory this process cannot read is
    /// skipped, not aborted over.
    /// </summary>
    private static List<string> FindFilesAcross(IEnumerable<string> directories, string pattern)
    {
        var found = new List<string>();
        foreach (var directory in directories)
        {
            foreach (var file in IncidentalFiles(directory, pattern, IncidentalScan))
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
    /// can be found so the tool layer reports a <c>NotFoundError</c>. A bare <em>name</em> is looked
    /// up by sweeping <paramref name="baseDirectory"/> recursively — the same anchor
    /// <see cref="AutoDiscover"/> walks — rather than reaching for the process working directory
    /// independently of the caller that already resolved one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the bare-name sweep is anchored that way. The two branches above it hand
    /// <paramref name="project"/> to <see cref="File.Exists(string)"/> and
    /// <see cref="Directory.Exists(string)"/>, which the CLR resolves against the <em>process</em>
    /// working directory — so a <em>relative</em> path argument still answers from there, not from
    /// <paramref name="baseDirectory"/>. In production the two are the same value (every caller of
    /// <see cref="ResolveTargetPath"/> passes <see cref="Directory.GetCurrentDirectory"/>); they
    /// diverge only under test.
    /// </para>
    /// <para>
    /// The two enumerations below deliberately use <em>different</em> overloads, and the asymmetry
    /// is the point: the caller named the directory in the first, and did not in the second.
    /// </para>
    /// </remarks>
    private static string ResolveProjectPath(string project, string baseDirectory)
    {
        if (File.Exists(project) && project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return project;
        }

        if (Directory.Exists(project))
        {
            // Deliberately the SearchOption overload, which does NOT ignore inaccessible paths: the
            // caller named this directory, so "I could not read it" is the answer they need.
            // Swallowing the UnauthorizedAccessException here would degrade it into the
            // FileNotFoundException below — "Project not found" — sending them to look for a missing
            // file instead of fixing a permission. ToolExecutionHelper.Classify maps the throw onto
            // AnalysisError, so the real message reaches the caller intact.
            //
            // Caveat, and it is a real limit rather than an oversight: this holds where an
            // unreadable directory still reports as existing, which is the Unix behavior. Windows
            // ACL-denies instead, and Directory.Exists is documented to answer false when the caller
            // lacks permission — so there the branch is skipped and such a path degrades to
            // NotFoundError after all. Closing that would need an attempted enumeration rather than
            // an Exists probe; it is not attempted here.
            var csprojFiles = Directory.GetFiles(project, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
            {
                return csprojFiles[0];
            }
        }

        // This sweep is incidental — an unreadable directory anywhere under the base directory has
        // nothing to do with resolving a project NAME — so aborting the whole lookup over one was
        // wrong regardless of how the resulting exception was labelled. IncidentalRecursiveScan
        // (see CreateIncidentalScan's doc) is the same settings as every other incidental scan here,
        // recursive — and IncidentalFiles applies the same shadow filter, so a name that happens to
        // spell a shadow's stem can never resolve to the shadow.
        //
        // IncidentalFiles streams (EnumerateFiles, not GetFiles) and stops at the first name match
        // instead of materializing every .csproj in the tree first. The scan runs on the
        // write-confirmation path before a human is prompted, so the early exit is worth keeping.
        var match = IncidentalFiles(baseDirectory, "*.csproj", IncidentalRecursiveScan)
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(project, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        throw new FileNotFoundException($"Project not found: {project}");
    }

    /// <summary>
    /// Walks from <paramref name="startPath"/> up through its ancestors looking for a <c>.sln</c>.
    /// An unreadable rung is skipped, not fatal: <see cref="Directory.GetParent(string)"/> needs no
    /// read permission on the child it is leaving, so the climb continues past it.
    /// </summary>
    private static string? FindSolutionFile(string startPath)
    {
        var directory = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);

        while (!string.IsNullOrEmpty(directory))
        {
            var slnFiles = Directory.GetFiles(directory, "*.sln", IncidentalScan);
            if (slnFiles.Length > 0)
            {
                return slnFiles[0];
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return null;
    }
}
