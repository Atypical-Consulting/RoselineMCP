using Microsoft.CodeAnalysis;
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

            if (!File.Exists(solutionPath))
            {
                throw new FileNotFoundException($"Solution file not found: {solutionPath}");
            }

            using var workspace = _msBuildService.CreateWorkspace();

            _logger.LogInformation("Loading solution: {Path}", solutionPath);
            var solution = await workspace.OpenSolutionAsync(solutionPath);

            var response = new AnalyzeSolutionResponse
            {
                Solution = Path.GetFileName(solutionPath),
                Projects = solution.Projects.Count()
            };

            var allDiagnostics = new List<DiagnosticDetail>();
            var summary = new DiagnosticSummary();

            foreach (var project in solution.Projects)
            {
                if (!_filterService.ShouldAnalyzeProject(project.Name, includePattern, excludePattern))
                {
                    continue;
                }

                _logger.LogInformation("Analyzing project: {ProjectName}", project.Name);

                var compilation = await project.GetCompilationAsync();
                if (compilation == null)
                {
                    _logger.LogWarning("Failed to get compilation for project: {ProjectName}", project.Name);
                    continue;
                }

                var diagnostics = compilation.GetDiagnostics()
                    .Where(d => _filterService.ShouldIncludeDiagnostic(d, severity))
                    .Take(maxDiagnostics);

                foreach (var diagnostic in diagnostics)
                {
                    UpdateSummary(summary, diagnostic.Severity);

                    if (allDiagnostics.Count < maxDiagnostics)
                    {
                        var location = diagnostic.Location.GetLineSpan();
                        allDiagnostics.Add(new DiagnosticDetail
                        {
                            Project = project.Name,
                            File = location.Path ?? "Unknown",
                            Line = location.StartLinePosition.Line + 1,
                            Column = location.StartLinePosition.Character + 1,
                            Id = diagnostic.Id,
                            Severity = diagnostic.Severity.ToString().ToLower(),
                            Message = diagnostic.GetMessage()
                        });
                    }
                }
            }

            response.DiagnosticSummary = summary;
            response.TopDiagnostics = allDiagnostics
                .OrderByDescending(d => _filterService.GetSeverityPriority(d.Severity))
                .ThenBy(d => d.File)
                .ThenBy(d => d.Line)
                .Take(maxDiagnostics)
                .ToList();

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze solution");
            throw;
        }
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
            var projectPath = ResolveProjectPath(project);

            using var workspace = _msBuildService.CreateWorkspace();

            _logger.LogInformation("Loading project: {Path}", projectPath);

            Microsoft.CodeAnalysis.Project? msProject = null;

            // Try to load as project file first
            if (File.Exists(projectPath) && projectPath.EndsWith(".csproj"))
            {
                msProject = await workspace.OpenProjectAsync(projectPath);
            }
            else
            {
                // Try to find project by name in solution
                var solutionPath = FindSolutionFile(projectPath);
                if (solutionPath != null)
                {
                    var solution = await workspace.OpenSolutionAsync(solutionPath);
                    msProject = solution.Projects.FirstOrDefault(p =>
                        p.Name.Equals(project, StringComparison.OrdinalIgnoreCase) ||
                        p.FilePath?.Contains(project) == true);
                }
            }

            if (msProject == null)
            {
                throw new InvalidOperationException($"Project not found: {project}");
            }

            var response = new ListDiagnosticsResponse
            {
                Project = msProject.Name
            };

            var compilation = await msProject.GetCompilationAsync();
            if (compilation == null)
            {
                _logger.LogWarning("Failed to get compilation for project: {ProjectName}", msProject.Name);
                return response;
            }

            var allDiagnostics = compilation.GetDiagnostics()
                .Where(d => !d.IsSuppressed)
                .Where(d => _filterService.FilterByIds(d, ids))
                .Where(d => _filterService.FilterByFiles(d, files))
                .ToList();

            response.TotalDiagnostics = allDiagnostics.Count;

            // Collect statistics
            var byId = new Dictionary<string, int>();
            var bySeverity = new Dictionary<string, int>();
            var fixableIds = new HashSet<string>();

            foreach (var diagnostic in allDiagnostics)
            {
                // Update ID stats
                if (!byId.ContainsKey(diagnostic.Id))
                    byId[diagnostic.Id] = 0;
                byId[diagnostic.Id]++;

                // Update severity stats
                var severityStr = diagnostic.Severity.ToString();
                if (!bySeverity.ContainsKey(severityStr))
                    bySeverity[severityStr] = 0;
                bySeverity[severityStr]++;

                // Check if fixable
                if (_filterService.IsFixableDiagnostic(diagnostic.Id))
                {
                    fixableIds.Add(diagnostic.Id);
                }
            }

            response.Stats = new DiagnosticStats
            {
                ById = byId,
                BySeverity = bySeverity
            };

            response.SuggestedFixableIds = fixableIds.OrderBy(id => id).ToList();

            // Add diagnostic details (limited by max)
            response.Diagnostics = allDiagnostics
                .Take(max)
                .Select(d =>
                {
                    var location = d.Location.GetLineSpan();
                    return new DiagnosticDetail
                    {
                        Project = msProject.Name,
                        File = location.Path ?? "Unknown",
                        Line = location.StartLinePosition.Line + 1,
                        Column = location.StartLinePosition.Character + 1,
                        Id = d.Id,
                        Severity = d.Severity.ToString().ToLower(),
                        Message = d.GetMessage()
                    };
                })
                .ToList();

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list diagnostics for project");
            throw;
        }
    }

    private string ResolveSolutionPath(string pathOrGit, string? branch)
    {
        if (pathOrGit.StartsWith("http://") || pathOrGit.StartsWith("https://"))
        {
            throw new NotImplementedException("Git repository cloning not yet implemented. Please provide a local path.");
        }

        if (Directory.Exists(pathOrGit))
        {
            var slnFiles = Directory.GetFiles(pathOrGit, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length == 0)
            {
                throw new FileNotFoundException($"No solution files found in directory: {pathOrGit}");
            }
            return slnFiles.First();
        }

        return pathOrGit;
    }

    private string ResolveProjectPath(string project)
    {
        // If it's already a full path to a .csproj file
        if (File.Exists(project) && project.EndsWith(".csproj"))
        {
            return project;
        }

        // If it's a directory, look for .csproj files
        if (Directory.Exists(project))
        {
            var csprojFiles = Directory.GetFiles(project, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
            {
                return csprojFiles.First();
            }
        }

        // Otherwise return as-is and let the caller handle finding it
        return project;
    }

    private string? FindSolutionFile(string startPath)
    {
        var directory = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);

        while (!string.IsNullOrEmpty(directory))
        {
            var slnFiles = Directory.GetFiles(directory!, "*.sln", SearchOption.TopDirectoryOnly);
            if (slnFiles.Length > 0)
            {
                return slnFiles.First();
            }

            var parent = Directory.GetParent(directory!);
            if (parent == null)
                break;

            directory = parent.FullName;
        }

        return null;
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