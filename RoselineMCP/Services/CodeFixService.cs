using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using System.Text;

namespace RoselineMCP.Services;

/// <summary>
/// Service for applying automated code fixes to C# projects using Roslyn code fix providers.
/// </summary>
public class CodeFixService : ICodeFixService
{
    private readonly ILogger<CodeFixService> _logger;
    private readonly ISolutionAnalyzerService _analyzerService;
    private readonly ICodeFixProviderFactory _codeFixProviderFactory;
    private readonly IDiffService _diffService;
    private readonly IMSBuildService _msBuildService;

    /// <summary>
    /// Initializes a new instance of the CodeFixService.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="analyzerService">Service for analyzing solutions.</param>
    /// <param name="codeFixProviderFactory">Factory for creating code fix providers.</param>
    /// <param name="diffService">Service for generating diffs.</param>
    /// <param name="msBuildService">Service for MSBuild operations.</param>
    public CodeFixService(
        ILogger<CodeFixService> logger,
        ISolutionAnalyzerService analyzerService,
        ICodeFixProviderFactory codeFixProviderFactory,
        IDiffService diffService,
        IMSBuildService msBuildService)
    {
        _logger = logger;
        _analyzerService = analyzerService;
        _codeFixProviderFactory = codeFixProviderFactory;
        _diffService = diffService;
        _msBuildService = msBuildService;
    }


    /// <inheritdoc/>
    public async Task<ApplyFixesResponse> ApplyFixesAsync(
        string project,
        List<string> ids,
        bool previewOnly = false)
    {
        var response = new ApplyFixesResponse
        {
            Project = project,
            PreviewOnly = previewOnly
        };

        try
        {
            // Create a temporary workspace
            using var workspace = _msBuildService.CreateWorkspace();

            workspace.WorkspaceFailed += (sender, e) =>
            {
                _logger.LogWarning("Workspace failed: {Message}", e.Diagnostic.Message);
            };

            // Load the project
            var projectPath = ResolveProjectPath(project);
            _logger.LogInformation("Loading project for fixes: {Path}", projectPath);

            var msProject = await workspace.OpenProjectAsync(projectPath);
            response.Project = msProject.Name;

            // Get the original solution text for diff generation
            var originalSolution = workspace.CurrentSolution;
            var originalTexts = new Dictionary<string, string>();

            foreach (var document in msProject.Documents)
            {
                var text = await document.GetTextAsync();
                originalTexts[document.FilePath!] = text.ToString();
            }

            // Apply fixes for each diagnostic ID
            var appliedFixes = new HashSet<string>();
            var changedDocuments = new HashSet<string>();
            var currentSolution = originalSolution;
            var fixCount = 0;

            foreach (var diagnosticId in ids)
            {
                _logger.LogInformation("Attempting to fix diagnostic: {Id}", diagnosticId);

                // Get the current project from the solution
                var currentProject = currentSolution.GetProject(msProject.Id);
                if (currentProject == null) continue;

                // Get compilation and diagnostics
                var compilation = await currentProject.GetCompilationAsync();
                if (compilation == null) continue;

                var diagnostics = compilation.GetDiagnostics()
                    .Where(d => d.Id == diagnosticId && !d.IsSuppressed)
                    .ToList();

                if (diagnostics.Count == 0)
                {
                    response.Notes.Add($"No diagnostics found for {diagnosticId}");
                    continue;
                }

                // Find code fix provider for this diagnostic
                var provider = _codeFixProviderFactory.GetProviderForDiagnostic(diagnosticId);
                if (provider == null)
                {
                    response.Notes.Add($"No code fix provider found for {diagnosticId}");
                    continue;
                }

                try
                {

                    // Group diagnostics by document
                    var diagnosticsByDocument = diagnostics
                        .Where(d => d.Location.SourceTree != null)
                        .GroupBy(d => currentProject.Documents.FirstOrDefault(doc =>
                            doc.FilePath == d.Location.SourceTree?.FilePath));

                    foreach (var group in diagnosticsByDocument.Where(g => g.Key != null))
                    {
                        var document = group.Key!;
                        var documentDiagnostics = group.ToList();

                        foreach (var diagnostic in documentDiagnostics)
                        {
                            var context = new CodeFixContext(
                                document,
                                diagnostic,
                                async (action, _) =>
                                {
                                    try
                                    {
                                        var operations = await action.GetOperationsAsync(CancellationToken.None);
                                        var operation = operations.OfType<ApplyChangesOperation>().FirstOrDefault();

                                        if (operation != null)
                                        {
                                            currentSolution = operation.ChangedSolution;
                                            changedDocuments.Add(document.FilePath!);
                                            fixCount++;
                                            _logger.LogDebug("Applied fix for {Id} in {File}",
                                                diagnosticId, document.Name);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning("Failed to apply fix for {Id}: {Message}",
                                            diagnosticId, ex.Message);
                                    }
                                },
                                CancellationToken.None);

                            await provider.RegisterCodeFixesAsync(context);
                        }
                    }

                    if (fixCount > 0)
                    {
                        appliedFixes.Add(diagnosticId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error applying fixes for diagnostic {Id}", diagnosticId);
                    response.Notes.Add($"Error fixing {diagnosticId}: {ex.Message}");
                }
            }

            response.FixersApplied = appliedFixes.ToList();
            response.FixedCount = fixCount;

            // Format the changed documents
            if (changedDocuments.Any())
            {
                _logger.LogInformation("Formatting {Count} changed documents", changedDocuments.Count);

                foreach (var filePath in changedDocuments)
                {
                    var document = currentSolution.Projects
                        .SelectMany(p => p.Documents)
                        .FirstOrDefault(d => d.FilePath == filePath);

                    if (document != null)
                    {
                        document = await Formatter.FormatAsync(document);
                        currentSolution = document.Project.Solution;
                    }
                }
            }

            // Generate patch
            if (changedDocuments.Any())
            {
                var patchBuilder = new StringBuilder();

                foreach (var filePath in changedDocuments.OrderBy(f => f))
                {
                    var relativePath = Path.GetRelativePath(Path.GetDirectoryName(projectPath)!, filePath);
                    response.ChangedFiles.Add(relativePath);

                    var newDocument = currentSolution.Projects
                        .SelectMany(p => p.Documents)
                        .FirstOrDefault(d => d.FilePath == filePath);

                    if (newDocument != null)
                    {
                        var newText = await newDocument.GetTextAsync();
                        var oldText = originalTexts.GetValueOrDefault(filePath, "");

                        var diff = _diffService.GenerateUnifiedDiff(
                            oldText,
                            newText.ToString(),
                            $"a/{relativePath}",
                            $"b/{relativePath}");

                        if (!string.IsNullOrWhiteSpace(diff))
                        {
                            patchBuilder.AppendLine(diff);
                        }
                    }
                }

                response.Patch = patchBuilder.ToString();
            }

            // Apply changes if not preview only
            if (!previewOnly && changedDocuments.Any())
            {
                _logger.LogInformation("Applying changes to {Count} files", changedDocuments.Count);

                foreach (var filePath in changedDocuments)
                {
                    var document = currentSolution.Projects
                        .SelectMany(p => p.Documents)
                        .FirstOrDefault(d => d.FilePath == filePath);

                    if (document != null)
                    {
                        var text = await document.GetTextAsync();
                        await File.WriteAllTextAsync(filePath, text.ToString());
                    }
                }

                response.Notes.Add($"Applied {fixCount} fixes to {changedDocuments.Count} files");
            }
            else if (previewOnly)
            {
                response.Notes.Add("Preview mode - no changes were saved to disk");
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply fixes");
            response.Notes.Add($"Error: {ex.Message}");
            return response;
        }
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

        // Try to find in current directory
        var currentDirFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj", SearchOption.AllDirectories);
        var match = currentDirFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(project, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            return match;
        }

        throw new FileNotFoundException($"Project not found: {project}");
    }

}