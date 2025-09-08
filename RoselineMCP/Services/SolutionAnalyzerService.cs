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
        int maxDiagnostics = 100)
    {
        try
        {
            var solutionPath = ResolveSolutionPath(pathOrGit, branch);
            ValidateSolutionPath(solutionPath);

            using var workspace = _msBuildService.CreateWorkspace();
            var solution = await LoadSolutionAsync(workspace, solutionPath);

            var analysisContext = new AnalysisContext
            {
                IncludePattern = includePattern,
                ExcludePattern = excludePattern,
                Severity = severity,
                MaxDiagnostics = maxDiagnostics
            };

            var (diagnostics, summary) = await AnalyzeProjectsAsync(solution, analysisContext);

            return BuildAnalyzeSolutionResponse(solutionPath, solution, diagnostics, summary, maxDiagnostics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze solution");
            throw;
        }
    }

    private void ValidateSolutionPath(string solutionPath)
    {
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException($"Solution file not found: {solutionPath}");
        }
    }

    private async Task<Solution> LoadSolutionAsync(MSBuildWorkspace workspace, string solutionPath)
    {
        _logger.LogInformation("Loading solution: {Path}", solutionPath);
        return await workspace.OpenSolutionAsync(solutionPath);
    }

    private async Task<(List<DiagnosticDetail> diagnostics, DiagnosticSummary summary)> AnalyzeProjectsAsync(
        Solution solution,
        AnalysisContext context)
    {
        var allDiagnostics = new List<DiagnosticDetail>();
        var summary = new DiagnosticSummary();

        foreach (var project in solution.Projects)
        {
            if (!_filterService.ShouldAnalyzeProject(project.Name, context.IncludePattern, context.ExcludePattern))
            {
                continue;
            }

            await AnalyzeProjectAsync(project, allDiagnostics, summary, context);
        }

        return (allDiagnostics, summary);
    }

    private async Task AnalyzeProjectAsync(
        Project project,
        List<DiagnosticDetail> allDiagnostics,
        DiagnosticSummary summary,
        AnalysisContext context)
    {
        _logger.LogInformation("Analyzing project: {ProjectName}", project.Name);

        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
        {
            _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
            return;
        }

        var diagnostics = GetFilteredDiagnostics(compilation, context);
        ProcessProjectDiagnostics(diagnostics, project.Name, allDiagnostics, summary, context.MaxDiagnostics);
    }

    private IEnumerable<Diagnostic> GetFilteredDiagnostics(Compilation compilation, AnalysisContext context)
    {
        return compilation.GetDiagnostics()
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
        int max = 100)
    {
        try
        {
            using var workspace = _msBuildService.CreateWorkspace();
            var msProject = await LoadProjectAsync(workspace, project);

            var compilation = await GetProjectCompilationAsync(msProject);
            if (compilation == null)
            {
                return new ListDiagnosticsResponse { Project = msProject.Name };
            }

            var allDiagnostics = GetProjectDiagnostics(compilation, ids, files);
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

    private async Task<Project> LoadProjectAsync(MSBuildWorkspace workspace, string project)
    {
        var projectPath = ResolveProjectPath(project);
        _logger.LogInformation("Loading project: {Path}", projectPath);

        var msProject = await TryLoadProjectDirectlyAsync(workspace, projectPath);
        if (msProject != null)
        {
            return msProject;
        }

        msProject = await TryLoadProjectFromSolutionAsync(workspace, projectPath, project);
        if (msProject == null)
        {
            throw new InvalidOperationException($"Project not found: {project}");
        }

        return msProject;
    }

    private async Task<Project?> TryLoadProjectDirectlyAsync(MSBuildWorkspace workspace, string projectPath)
    {
        if (File.Exists(projectPath) && projectPath.EndsWith(".csproj"))
        {
            return await workspace.OpenProjectAsync(projectPath);
        }
        return null;
    }

    private async Task<Project?> TryLoadProjectFromSolutionAsync(MSBuildWorkspace workspace, string projectPath, string projectName)
    {
        var solutionPath = FindSolutionFile(projectPath);
        if (solutionPath == null)
        {
            return null;
        }

        var solution = await workspace.OpenSolutionAsync(solutionPath);
        return FindProjectInSolution(solution, projectName);
    }

    private Project? FindProjectInSolution(Solution solution, string projectName)
    {
        return solution.Projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) ||
            p.FilePath?.Contains(projectName) == true);
    }

    private async Task<Compilation?> GetProjectCompilationAsync(Project project)
    {
        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
        {
            _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
        }
        return compilation;
    }

    private List<Diagnostic> GetProjectDiagnostics(Compilation compilation, List<string>? ids, List<string>? files)
    {
        return compilation.GetDiagnostics()
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

    private string ResolveSolutionPath(string pathOrGit, string? branch)
    {
        ValidateNotGitRepository(pathOrGit);
        
        if (Directory.Exists(pathOrGit))
        {
            return FindSolutionInDirectory(pathOrGit);
        }

        return pathOrGit;
    }

    private void ValidateNotGitRepository(string path)
    {
        if (IsGitUrl(path))
        {
            throw new NotImplementedException("Git repository cloning not yet implemented. Please provide a local path.");
        }
    }

    private bool IsGitUrl(string path)
    {
        return path.StartsWith("http://") || path.StartsWith("https://");
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