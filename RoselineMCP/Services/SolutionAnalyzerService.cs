using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Service for analyzing C# solutions and projects for diagnostics using Roslyn.
/// </summary>
public class SolutionAnalyzerService : ISolutionAnalyzerService
{
    /// <summary>
    /// Maximum time to allow a shallow Git clone to run before it is aborted.
    /// Prevents a slow or unresponsive remote from hanging the tool indefinitely.
    /// </summary>
    private static readonly TimeSpan GitCloneTimeout = TimeSpan.FromSeconds(120);

    private readonly ILogger<SolutionAnalyzerService> _logger;
    private readonly IMSBuildService _msBuildService;
    private readonly IDiagnosticFilterService _filterService;

    /// <summary>
    /// Initializes a new instance of the SolutionAnalyzerService.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="msBuildService">Service for MSBuild operations.</param>
    /// <param name="filterService">Service for filtering diagnostics.</param>
    public SolutionAnalyzerService(
        ILogger<SolutionAnalyzerService> logger,
        IMSBuildService msBuildService,
        IDiagnosticFilterService filterService)
    {
        _logger = logger;
        _msBuildService = msBuildService;
        _filterService = filterService;
    }

    /// <inheritdoc/>
    public async Task<AnalyzeSolutionResponse> AnalyzeSolutionAsync(
        string pathOrGit,
        string? branch = null,
        string? includePattern = null,
        string? excludePattern = null,
        string? severity = null,
        int maxDiagnostics = 100,
        CancellationToken cancellationToken = default)
    {
        string? clonedDirectory = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (solutionPath, cloneDirectory) = await ResolveSolutionPathAsync(pathOrGit, branch, cancellationToken);
            clonedDirectory = cloneDirectory;
            ValidateSolutionPath(solutionPath);

            using var workspace = _msBuildService.CreateWorkspace();
            var solution = await LoadSolutionAsync(workspace, solutionPath, cancellationToken);

            var analysisContext = new AnalysisContext
            {
                IncludePattern = includePattern,
                ExcludePattern = excludePattern,
                Severity = severity,
                MaxDiagnostics = maxDiagnostics
            };

            var (diagnostics, summary) = await AnalyzeProjectsAsync(solution, analysisContext, cancellationToken);

            return BuildAnalyzeSolutionResponse(solutionPath, solution, diagnostics, summary, maxDiagnostics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze solution");
            throw;
        }
        finally
        {
            if (clonedDirectory != null)
            {
                SafeDeleteDirectory(clonedDirectory);
            }
        }
    }

    private void ValidateSolutionPath(string solutionPath)
    {
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException($"Solution file not found: {solutionPath}");
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Requires real MSBuild workspace — integration test territory")]
    private async Task<Solution> LoadSolutionAsync(MSBuildWorkspace workspace, string solutionPath, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Loading solution: {Path}", solutionPath);
        return await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
    }

    private async Task<(List<DiagnosticDetail> diagnostics, DiagnosticSummary summary)> AnalyzeProjectsAsync(
        Solution solution,
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        var allDiagnostics = new List<DiagnosticDetail>();
        var summary = new DiagnosticSummary();

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_filterService.ShouldAnalyzeProject(project.Name, context.IncludePattern, context.ExcludePattern))
            {
                continue;
            }

            await AnalyzeProjectAsync(project, allDiagnostics, summary, context, cancellationToken);
        }

        return (allDiagnostics, summary);
    }

    private async Task AnalyzeProjectAsync(
        Project project,
        List<DiagnosticDetail> allDiagnostics,
        DiagnosticSummary summary,
        AnalysisContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Analyzing project: {ProjectName}", project.Name);

        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
        {
            _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
            return;
        }

        var diagnostics = GetFilteredDiagnostics(compilation, context, cancellationToken);
        ProcessProjectDiagnostics(diagnostics, project.Name, allDiagnostics, summary, context.MaxDiagnostics);
    }

    private IEnumerable<Diagnostic> GetFilteredDiagnostics(Compilation compilation, AnalysisContext context, CancellationToken cancellationToken)
    {
        return compilation.GetDiagnostics(cancellationToken)
            .Where(d => _filterService.ShouldIncludeDiagnostic(d, context.Severity))
            .Take(context.MaxDiagnostics);
    }

    private void ProcessProjectDiagnostics(
        IEnumerable<Diagnostic> diagnostics,
        string projectName,
        List<DiagnosticDetail> allDiagnostics,
        DiagnosticSummary summary,
        int maxDiagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            UpdateSummary(summary, diagnostic.Severity);

            if (allDiagnostics.Count < maxDiagnostics)
            {
                allDiagnostics.Add(CreateDiagnosticDetail(diagnostic, projectName));
            }
        }
    }

    private DiagnosticDetail CreateDiagnosticDetail(Diagnostic diagnostic, string projectName)
    {
        var location = diagnostic.Location.GetLineSpan();
        return new DiagnosticDetail
        {
            Project = projectName,
            File = location.Path ?? "Unknown",
            Line = location.StartLinePosition.Line + 1,
            Column = location.StartLinePosition.Character + 1,
            Id = diagnostic.Id,
            Severity = diagnostic.Severity.ToString().ToLower(),
            Message = diagnostic.GetMessage()
        };
    }

    private AnalyzeSolutionResponse BuildAnalyzeSolutionResponse(
        string solutionPath,
        Solution solution,
        List<DiagnosticDetail> diagnostics,
        DiagnosticSummary summary,
        int maxDiagnostics)
    {
        return new AnalyzeSolutionResponse
        {
            Solution = Path.GetFileName(solutionPath),
            Projects = solution.Projects.Count(),
            DiagnosticSummary = summary,
            TopDiagnostics = OrderDiagnostics(diagnostics, maxDiagnostics)
        };
    }

    private List<DiagnosticDetail> OrderDiagnostics(List<DiagnosticDetail> diagnostics, int maxDiagnostics)
    {
        return diagnostics
            .OrderByDescending(d => _filterService.GetSeverityPriority(d.Severity))
            .ThenBy(d => d.File)
            .ThenBy(d => d.Line)
            .Take(maxDiagnostics)
            .ToList();
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    private class AnalysisContext
    {
        public string? IncludePattern { get; set; }
        public string? ExcludePattern { get; set; }
        public string? Severity { get; set; }
        public int MaxDiagnostics { get; set; }
    }

    /// <inheritdoc/>
    public async Task<ListDiagnosticsResponse> ListDiagnosticsAsync(
        string project,
        List<string>? ids = null,
        List<string>? files = null,
        int max = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var workspace = _msBuildService.CreateWorkspace();
            var msProject = await LoadProjectAsync(workspace, project, cancellationToken);

            var compilation = await GetProjectCompilationAsync(msProject, cancellationToken);
            if (compilation == null)
            {
                return new ListDiagnosticsResponse { Project = msProject.Name };
            }

            var allDiagnostics = GetProjectDiagnostics(compilation, ids, files, cancellationToken);
            var stats = CollectDiagnosticStatistics(allDiagnostics);
            var diagnosticDetails = CreateDiagnosticDetails(allDiagnostics, msProject.Name, max);

            return new ListDiagnosticsResponse
            {
                Project = msProject.Name,
                TotalDiagnostics = allDiagnostics.Count,
                Stats = stats.Stats,
                SuggestedFixableIds = stats.FixableIds,
                Diagnostics = diagnosticDetails
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list diagnostics for project");
            throw;
        }
    }

    private async Task<Project> LoadProjectAsync(MSBuildWorkspace workspace, string project, CancellationToken cancellationToken)
    {
        var projectPath = ResolveProjectPath(project);
        _logger.LogInformation("Loading project: {Path}", projectPath);

        var msProject = await TryLoadProjectDirectlyAsync(workspace, projectPath, cancellationToken);
        if (msProject != null)
        {
            return msProject;
        }

        msProject = await TryLoadProjectFromSolutionAsync(workspace, projectPath, project, cancellationToken);
        if (msProject == null)
        {
            throw new InvalidOperationException($"Project not found: {project}");
        }

        return msProject;
    }

    private async Task<Project?> TryLoadProjectDirectlyAsync(MSBuildWorkspace workspace, string projectPath, CancellationToken cancellationToken)
    {
        if (File.Exists(projectPath) && projectPath.EndsWith(".csproj"))
        {
            return await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
        }
        return null;
    }

    private async Task<Project?> TryLoadProjectFromSolutionAsync(MSBuildWorkspace workspace, string projectPath, string projectName, CancellationToken cancellationToken)
    {
        var solutionPath = FindSolutionFile(projectPath);
        if (solutionPath == null)
        {
            return null;
        }

        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
        return FindProjectInSolution(solution, projectName);
    }

    private Project? FindProjectInSolution(Solution solution, string projectName)
    {
        return solution.Projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) ||
            p.FilePath?.Contains(projectName) == true);
    }

    private async Task<Compilation?> GetProjectCompilationAsync(Project project, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
        {
            _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
        }
        return compilation;
    }

    private List<Diagnostic> GetProjectDiagnostics(Compilation compilation, List<string>? ids, List<string>? files, CancellationToken cancellationToken)
    {
        return compilation.GetDiagnostics(cancellationToken)
            .Where(d => !d.IsSuppressed)
            .Where(d => _filterService.FilterByIds(d, ids))
            .Where(d => _filterService.FilterByFiles(d, files))
            .ToList();
    }

    private (DiagnosticStats Stats, List<string> FixableIds) CollectDiagnosticStatistics(List<Diagnostic> diagnostics)
    {
        var byId = new Dictionary<string, int>();
        var bySeverity = new Dictionary<string, int>();
        var fixableIds = new HashSet<string>();

        foreach (var diagnostic in diagnostics)
        {
            UpdateIdStatistics(byId, diagnostic.Id);
            UpdateSeverityStatistics(bySeverity, diagnostic.Severity);
            CheckFixability(fixableIds, diagnostic.Id);
        }

        var stats = new DiagnosticStats
        {
            ById = byId,
            BySeverity = bySeverity
        };

        return (stats, fixableIds.OrderBy(id => id).ToList());
    }

    private void UpdateIdStatistics(Dictionary<string, int> byId, string diagnosticId)
    {
        if (!byId.ContainsKey(diagnosticId))
        {
            byId[diagnosticId] = 0;
        }
        byId[diagnosticId]++;
    }

    private void UpdateSeverityStatistics(Dictionary<string, int> bySeverity, DiagnosticSeverity severity)
    {
        var severityStr = severity.ToString();
        if (!bySeverity.ContainsKey(severityStr))
        {
            bySeverity[severityStr] = 0;
        }
        bySeverity[severityStr]++;
    }

    private void CheckFixability(HashSet<string> fixableIds, string diagnosticId)
    {
        if (_filterService.IsFixableDiagnostic(diagnosticId))
        {
            fixableIds.Add(diagnosticId);
        }
    }

    private List<DiagnosticDetail> CreateDiagnosticDetails(List<Diagnostic> diagnostics, string projectName, int max)
    {
        return diagnostics
            .Take(max)
            .Select(d => CreateDiagnosticDetail(d, projectName))
            .ToList();
    }

    /// <summary>
    /// Resolves <paramref name="pathOrGit"/> to a local solution file path. If it is a Git URL,
    /// the repository is shallow-cloned into a fresh temp directory first; the caller is
    /// responsible for deleting that directory (returned as <c>ClonedDirectory</c>) once it is
    /// no longer needed.
    /// </summary>
    private async Task<(string SolutionPath, string? ClonedDirectory)> ResolveSolutionPathAsync(
        string pathOrGit,
        string? branch,
        CancellationToken cancellationToken)
    {
        if (IsGitUrl(pathOrGit))
        {
            await EnsureGitUrlIsNotInternalAsync(pathOrGit, cancellationToken);
            var clonedDirectory = await CloneGitRepositoryAsync(pathOrGit, branch, cancellationToken);
            return (FindSolutionInDirectory(clonedDirectory), clonedDirectory);
        }

        if (Directory.Exists(pathOrGit))
        {
            return (FindSolutionInDirectory(pathOrGit), null);
        }

        return (pathOrGit, null);
    }

    /// <summary>
    /// Narrow, production-facing gate: only http(s) URLs are ever treated as Git remotes.
    /// Anything else (file://, ssh://, git://, plain local paths, ...) falls through to normal
    /// local-path resolution, which safely fails with FileNotFoundException if it doesn't exist —
    /// there is no code path that lets a non-http(s) scheme reach <see cref="CloneGitRepositoryAsync"/>.
    /// </summary>
    private bool IsGitUrl(string path)
    {
        return path.StartsWith("http://") || path.StartsWith("https://");
    }

    /// <summary>
    /// SSRF guard: resolves the host of an http(s) Git URL and rejects it if any resolved
    /// address is loopback, link-local, or in a private (RFC1918) range — e.g. the cloud
    /// metadata endpoint, localhost, or internal network addresses. Applied only to the
    /// production http(s) path (called from <see cref="ResolveSolutionPathAsync"/>), not to
    /// <see cref="CloneGitRepositoryAsync"/> itself, which stays exercisable directly against
    /// local repository paths in tests.
    ///
    /// Note: this checks the address(es) resolved at validation time. It does not eliminate a
    /// DNS-rebinding TOCTOU window, since <c>git</c> itself re-resolves the host at connect
    /// time; closing that gap fully would require routing the clone through a pinned
    /// connection rather than the system <c>git</c> executable. See SECURITY.md.
    /// </summary>
    private static async Task EnsureGitUrlIsNotInternalAsync(string gitUrl, CancellationToken cancellationToken)
    {
        Uri uri;
        try
        {
            uri = new Uri(gitUrl);
        }
        catch (UriFormatException ex)
        {
            throw new InvalidOperationException($"Invalid Git URL: {gitUrl}", ex);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new InvalidOperationException($"Could not resolve host for Git URL: {uri.Host}", ex);
        }

        if (addresses.Length == 0 || Array.Exists(addresses, IsInternalAddress))
        {
            throw new InvalidOperationException(
                $"Refusing to clone Git URL '{gitUrl}': host '{uri.Host}' resolves to an internal, loopback, or link-local address.");
        }
    }

    private static bool IsInternalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10                                   // 10.0.0.0/8
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)  // 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168)              // 192.168.0.0/16
                || (bytes[0] == 169 && bytes[1] == 254);             // 169.254.0.0/16 (link-local)
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }

        return false;
    }

    /// <summary>
    /// Performs a shallow, read-only clone of <paramref name="gitUrl"/> into a freshly created
    /// temp directory using the system <c>git</c> executable (no library dependency). Intentionally
    /// does not re-validate the URL scheme itself — that gate lives in <see cref="IsGitUrl"/> — so
    /// this method can also be exercised directly (e.g. via reflection in tests) against local
    /// repository paths without weakening the production http(s)-only restriction.
    /// </summary>
    private async Task<string> CloneGitRepositoryAsync(string gitUrl, string? branch, CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"roselinemcp-clone-{Guid.NewGuid():N}");
        CreateCloneDirectory(tempDir);

        // Everything from here on can throw for reasons outside our control (missing git
        // binary, cancellation, non-zero exit code, ...). Whatever the failure, the temp
        // directory we just created must not be left behind.
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("clone");
            startInfo.ArgumentList.Add("--depth");
            startInfo.ArgumentList.Add("1");
            if (!string.IsNullOrWhiteSpace(branch))
            {
                startInfo.ArgumentList.Add("--branch");
                startInfo.ArgumentList.Add(branch);
            }
            startInfo.ArgumentList.Add(gitUrl);
            startInfo.ArgumentList.Add(tempDir);
            // Never let a private/misconfigured remote hang the process waiting on a credential prompt.
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            _logger.LogInformation("Cloning Git repository (branch: {Branch})", branch ?? "default");

            using var process = new Process { StartInfo = startInfo };
            var stdErr = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stdErr.AppendLine(e.Data);
                }
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(GitCloneTimeout);

            try
            {
                process.Start();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new TimeoutException(
                    $"Cloning the Git repository timed out after {GitCloneTimeout.TotalSeconds:N0} seconds.");
            }

            if (process.ExitCode != 0)
            {
                var details = stdErr.Length > 0 ? stdErr.ToString().Trim() : $"git exited with code {process.ExitCode}";
                throw new InvalidOperationException($"Failed to clone Git repository: {details}");
            }

            return tempDir;
        }
        catch
        {
            SafeDeleteDirectory(tempDir);
            throw;
        }
    }

    /// <summary>
    /// Creates the clone temp directory with owner-only permissions on non-Windows platforms,
    /// to avoid other local users on shared hosts being able to read cloned source while the
    /// clone is in progress. <c>UnixFileMode</c>-based creation throws
    /// <see cref="PlatformNotSupportedException"/> on Windows, hence the explicit guard.
    /// </summary>
    private static void CreateCloneDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort — the process may have already exited between the check and the kill.
        }
    }

    private void SafeDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                // Git marks object/pack files read-only on Windows. Directory.Delete honors
                // that attribute (unlike a Unix rm -rf, which only cares about the containing
                // directory's permissions) and throws UnauthorizedAccessException, so a cloned
                // repository's own .git directory must be cleared of ReadOnly first.
                ClearReadOnlyAttributes(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Best-effort cleanup — don't fail the caller's operation over cleanup issues,
            // but do surface it so operators can notice orphaned temp directories.
            _logger.LogWarning(ex, "Failed to delete temporary directory: {Directory}", directory);
        }
    }

    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private string FindSolutionInDirectory(string directory)
    {
        var slnFiles = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length == 0)
        {
            throw new FileNotFoundException($"No solution files found in directory: {directory}");
        }
        return slnFiles.First();
    }

    private string ResolveProjectPath(string project)
    {
        if (IsValidProjectFile(project))
        {
            return project;
        }

        var projectFromDirectory = TryFindProjectInDirectory(project);
        return projectFromDirectory ?? project;
    }

    private bool IsValidProjectFile(string path)
    {
        return File.Exists(path) && path.EndsWith(".csproj");
    }

    private string? TryFindProjectInDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        var csprojFiles = Directory.GetFiles(path, "*.csproj", SearchOption.TopDirectoryOnly);
        return csprojFiles.Length > 0 ? csprojFiles.First() : null;
    }

    private string? FindSolutionFile(string startPath)
    {
        var directory = GetStartDirectory(startPath);
        return SearchForSolutionInParentDirectories(directory);
    }

    private string? GetStartDirectory(string path)
    {
        return Directory.Exists(path) ? path : Path.GetDirectoryName(path);
    }

    private string? SearchForSolutionInParentDirectories(string? directory)
    {
        while (!string.IsNullOrEmpty(directory))
        {
            var solutionFile = TryFindSolutionInDirectory(directory);
            if (solutionFile != null)
            {
                return solutionFile;
            }

            directory = GetParentDirectory(directory);
        }

        return null;
    }

    private string? TryFindSolutionInDirectory(string directory)
    {
        var slnFiles = Directory.GetFiles(directory, "*.sln", SearchOption.TopDirectoryOnly);
        return slnFiles.Length > 0 ? slnFiles.First() : null;
    }

    private string? GetParentDirectory(string directory)
    {
        return Directory.GetParent(directory)?.FullName;
    }

    private void UpdateSummary(DiagnosticSummary summary, DiagnosticSeverity severity)
    {
        switch (severity)
        {
            case DiagnosticSeverity.Error:
                summary.Error++;
                break;
            case DiagnosticSeverity.Warning:
                summary.Warning++;
                break;
            case DiagnosticSeverity.Info:
                summary.Info++;
                break;
            case DiagnosticSeverity.Hidden:
                summary.Hidden++;
                break;
        }
    }
}