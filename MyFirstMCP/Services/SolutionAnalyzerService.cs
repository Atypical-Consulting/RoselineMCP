using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using MyFirstMCP.Models;
using System.Collections.Immutable;

namespace MyFirstMCP.Services;

public class SolutionAnalyzerService
{
    private readonly ILogger<SolutionAnalyzerService> _logger;
    private static bool _msBuildRegistered = false;
    private static readonly object _msBuildLock = new();

    public SolutionAnalyzerService(ILogger<SolutionAnalyzerService> logger)
    {
        _logger = logger;
        EnsureMSBuildRegistered();
    }

    private void EnsureMSBuildRegistered()
    {
        lock (_msBuildLock)
        {
            if (!_msBuildRegistered)
            {
                try
                {
                    var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
                    if (instances.Length > 0)
                    {
                        MSBuildLocator.RegisterInstance(instances.First());
                        _msBuildRegistered = true;
                        _logger.LogInformation("MSBuild registered successfully");
                    }
                    else
                    {
                        _logger.LogWarning("No MSBuild instances found");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to register MSBuild");
                }
            }
        }
    }

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

            using var workspace = MSBuildWorkspace.Create();
            
            workspace.WorkspaceFailed += (sender, e) =>
            {
                _logger.LogWarning("Workspace failed: {Message}", e.Diagnostic.Message);
            };

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
                if (!ShouldAnalyzeProject(project.Name, includePattern, excludePattern))
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
                    .Where(d => ShouldIncludeDiagnostic(d, severity))
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
                .OrderByDescending(d => GetSeverityPriority(d.Severity))
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

    private bool ShouldAnalyzeProject(string projectName, string? includePattern, string? excludePattern)
    {
        if (!string.IsNullOrEmpty(excludePattern) && projectName.Contains(excludePattern))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(includePattern) && !projectName.Contains(includePattern))
        {
            return false;
        }

        return true;
    }

    private bool ShouldIncludeDiagnostic(Diagnostic diagnostic, string? severityFilter)
    {
        if (diagnostic.IsSuppressed)
        {
            return false;
        }

        if (string.IsNullOrEmpty(severityFilter))
        {
            return true;
        }

        var requestedSeverity = Enum.Parse<DiagnosticSeverity>(severityFilter, true);
        return diagnostic.Severity >= requestedSeverity;
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

    private int GetSeverityPriority(string severity)
    {
        return severity.ToLower() switch
        {
            "error" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0
        };
    }
}