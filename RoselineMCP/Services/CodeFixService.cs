using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoselineMCP.Models;
using System.Reflection;
using System.Text;

namespace RoselineMCP.Services;

public class CodeFixService
{
    private readonly ILogger<CodeFixService> _logger;
    private readonly SolutionAnalyzerService _analyzerService;
    private static readonly Dictionary<string, Type> _codeFixProviders = new();
    private static bool _providersLoaded = false;

    public CodeFixService(ILogger<CodeFixService> logger, SolutionAnalyzerService analyzerService)
    {
        _logger = logger;
        _analyzerService = analyzerService;
        LoadCodeFixProviders();
    }

    private void LoadCodeFixProviders()
    {
        if (_providersLoaded) return;

        try
        {
            // Load built-in code fix providers
            var assemblies = new List<Assembly> { typeof(CodeFixProvider).Assembly };
            
            try
            {
                assemblies.Add(Assembly.Load("Microsoft.CodeAnalysis.Features"));
                assemblies.Add(Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"));
            }
            catch { }
            
            try
            {
                assemblies.Add(Assembly.Load("Roslynator.CodeFixes"));
            }
            catch { }

            foreach (var assembly in assemblies.Where(a => a != null))
            {
                try
                {
                    var types = assembly.GetTypes()
                        .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(CodeFixProvider)));

                    foreach (var type in types)
                    {
                        try
                        {
                            var instance = Activator.CreateInstance(type) as CodeFixProvider;
                            if (instance != null)
                            {
                                foreach (var id in instance.FixableDiagnosticIds)
                                {
                                    if (!_codeFixProviders.ContainsKey(id))
                                    {
                                        _codeFixProviders[id] = type;
                                        _logger.LogDebug("Registered code fix provider for {DiagnosticId}: {Provider}", 
                                            id, type.Name);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("Could not instantiate code fix provider {Type}: {Message}", 
                                type.Name, ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Error loading code fix providers from assembly {Assembly}: {Message}", 
                        assembly.FullName, ex.Message);
                }
            }

            _providersLoaded = true;
            _logger.LogInformation("Loaded {Count} code fix providers", _codeFixProviders.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load code fix providers");
        }
    }

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
            using var workspace = MSBuildWorkspace.Create();
            
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
                if (!_codeFixProviders.TryGetValue(diagnosticId, out var providerType))
                {
                    response.Notes.Add($"No code fix provider found for {diagnosticId}");
                    continue;
                }

                try
                {
                    var provider = Activator.CreateInstance(providerType) as CodeFixProvider;
                    if (provider == null) continue;

                    // Group diagnostics by document
                    var diagnosticsByDocument = diagnostics
                        .Where(d => d.Location.SourceTree != null)
                        .GroupBy(d => currentProject.Documents.FirstOrDefault(doc => 
                            doc.FilePath == d.Location.SourceTree.FilePath));

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
                        
                        var diff = GenerateUnifiedDiff(
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

    private string GenerateUnifiedDiff(string oldText, string newText, string oldPath, string newPath)
    {
        var diffBuilder = new InlineDiffBuilder(new Differ());
        var diff = diffBuilder.BuildDiffModel(oldText, newText);
        
        if (!diff.HasDifferences)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"--- {oldPath}");
        sb.AppendLine($"+++ {newPath}");
        
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');
        
        int contextLines = 3;
        var chunks = new List<DiffChunk>();
        DiffChunk? currentChunk = null;
        
        for (int i = 0; i < diff.Lines.Count; i++)
        {
            var line = diff.Lines[i];
            
            if (line.Type != ChangeType.Unchanged)
            {
                // Start a new chunk or extend current one
                if (currentChunk == null || i > currentChunk.EndIndex + contextLines)
                {
                    currentChunk = new DiffChunk
                    {
                        StartIndex = Math.Max(0, i - contextLines),
                        EndIndex = Math.Min(diff.Lines.Count - 1, i + contextLines)
                    };
                    chunks.Add(currentChunk);
                }
                else
                {
                    currentChunk.EndIndex = Math.Min(diff.Lines.Count - 1, i + contextLines);
                }
            }
        }
        
        foreach (var chunk in chunks)
        {
            var oldStart = chunk.StartIndex + 1;
            var oldCount = diff.Lines
                .Skip(chunk.StartIndex)
                .Take(chunk.EndIndex - chunk.StartIndex + 1)
                .Count(l => l.Type != ChangeType.Inserted);
            
            var newStart = chunk.StartIndex + 1;
            var newCount = diff.Lines
                .Skip(chunk.StartIndex)
                .Take(chunk.EndIndex - chunk.StartIndex + 1)
                .Count(l => l.Type != ChangeType.Deleted);
            
            sb.AppendLine($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@");
            
            for (int i = chunk.StartIndex; i <= chunk.EndIndex; i++)
            {
                var line = diff.Lines[i];
                switch (line.Type)
                {
                    case ChangeType.Unchanged:
                        sb.AppendLine($" {line.Text}");
                        break;
                    case ChangeType.Deleted:
                        sb.AppendLine($"-{line.Text}");
                        break;
                    case ChangeType.Inserted:
                        sb.AppendLine($"+{line.Text}");
                        break;
                }
            }
        }
        
        return sb.ToString();
    }
    
    private class DiffChunk
    {
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
    }
}