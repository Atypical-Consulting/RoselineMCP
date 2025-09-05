using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using MyFirstMCP.Models;

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

    public async Task<ListDiagnosticsResponse> ListDiagnosticsAsync(
        string project,
        List<string>? ids = null,
        List<string>? files = null,
        int max = 100)
    {
        try
        {
            var projectPath = ResolveProjectPath(project);
            
            using var workspace = MSBuildWorkspace.Create();
            
            workspace.WorkspaceFailed += (sender, e) =>
            {
                _logger.LogWarning("Workspace failed: {Message}", e.Diagnostic.Message);
            };

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
                .Where(d => FilterByIds(d, ids))
                .Where(d => FilterByFiles(d, files))
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

                // Check if fixable (common fixable diagnostic IDs)
                if (IsFixableDiagnostic(diagnostic.Id))
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

    private bool FilterByIds(Diagnostic diagnostic, List<string>? ids)
    {
        if (ids == null || ids.Count == 0)
            return true;
            
        return ids.Contains(diagnostic.Id);
    }

    private bool FilterByFiles(Diagnostic diagnostic, List<string>? files)
    {
        if (files == null || files.Count == 0)
            return true;
            
        var location = diagnostic.Location.GetLineSpan();
        if (location.Path == null)
            return false;
            
        return files.Any(f => location.Path.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsFixableDiagnostic(string id)
    {
        // Common fixable diagnostic IDs
        var fixableIds = new HashSet<string>
        {
            // Roslynator
            "RCS1001", "RCS1003", "RCS1018", "RCS1036", "RCS1037", "RCS1058", "RCS1059", "RCS1060",
            "RCS1061", "RCS1068", "RCS1069", "RCS1070", "RCS1071", "RCS1074", "RCS1077", "RCS1085",
            "RCS1097", "RCS1098", "RCS1099", "RCS1102", "RCS1103", "RCS1104", "RCS1105", "RCS1106",
            "RCS1118", "RCS1123", "RCS1126", "RCS1128", "RCS1132", "RCS1133", "RCS1134", "RCS1138",
            "RCS1139", "RCS1140", "RCS1141", "RCS1142", "RCS1143", "RCS1145", "RCS1146", "RCS1151",
            "RCS1155", "RCS1156", "RCS1157", "RCS1158", "RCS1159", "RCS1160", "RCS1161", "RCS1162",
            "RCS1163", "RCS1164", "RCS1168", "RCS1169", "RCS1170", "RCS1171", "RCS1172", "RCS1173",
            "RCS1174", "RCS1175", "RCS1180", "RCS1181", "RCS1182", "RCS1186", "RCS1187", "RCS1188",
            "RCS1189", "RCS1190", "RCS1191", "RCS1192", "RCS1193", "RCS1194", "RCS1195", "RCS1196",
            "RCS1197", "RCS1198", "RCS1199", "RCS1200", "RCS1201", "RCS1202", "RCS1203", "RCS1204",
            "RCS1205", "RCS1206", "RCS1207", "RCS1208", "RCS1209", "RCS1210", "RCS1211", "RCS1212",
            "RCS1213", "RCS1214", "RCS1215", "RCS1216", "RCS1217", "RCS1218", "RCS1220", "RCS1221",
            
            // StyleCop
            "SA1000", "SA1001", "SA1002", "SA1003", "SA1004", "SA1005", "SA1006", "SA1007", "SA1008",
            "SA1009", "SA1010", "SA1011", "SA1012", "SA1013", "SA1014", "SA1015", "SA1016", "SA1017",
            "SA1018", "SA1019", "SA1020", "SA1021", "SA1022", "SA1023", "SA1024", "SA1025", "SA1026",
            "SA1027", "SA1028", "SA1100", "SA1101", "SA1106", "SA1107", "SA1108", "SA1110", "SA1111",
            "SA1112", "SA1113", "SA1114", "SA1115", "SA1116", "SA1117", "SA1118", "SA1119", "SA1120",
            "SA1121", "SA1122", "SA1123", "SA1124", "SA1125", "SA1127", "SA1128", "SA1129", "SA1130",
            "SA1131", "SA1132", "SA1133", "SA1134", "SA1135", "SA1136", "SA1137", "SA1139", "SA1141",
            "SA1142", "SA1200", "SA1201", "SA1202", "SA1203", "SA1204", "SA1205", "SA1206", "SA1207",
            "SA1208", "SA1209", "SA1210", "SA1211", "SA1212", "SA1213", "SA1214", "SA1216", "SA1217",
            "SA1300", "SA1302", "SA1303", "SA1304", "SA1305", "SA1306", "SA1307", "SA1308", "SA1309",
            "SA1310", "SA1311", "SA1312", "SA1313", "SA1314", "SA1316", "SA1400", "SA1401", "SA1402",
            
            // Common C# compiler warnings that can be fixed
            "CS0168", // Variable declared but never used
            "CS0219", // Variable assigned but never used
            "CS0414", // Field assigned but never used
            "CS0649", // Field never assigned
            "CS1591", // Missing XML comment
            "CS8019", // Unnecessary using directive
            "IDE0001", // Simplify name
            "IDE0002", // Simplify member access
            "IDE0003", // Remove this or Me qualification
            "IDE0004", // Remove unnecessary cast
            "IDE0005", // Remove unnecessary import
            "IDE0007", // Use var
            "IDE0008", // Use explicit type
            "IDE0009", // Add this or Me qualification
            "IDE0010", // Add missing cases to switch statement
            "IDE0011", // Add braces
            "IDE0016", // Use throw expression
            "IDE0017", // Use object initializers
            "IDE0018", // Variable declaration can be inlined
            "IDE0019", // Use pattern matching
            "IDE0020", // Use pattern matching
            "IDE0021", // Use expression body for constructors
            "IDE0022", // Use expression body for methods
            "IDE0023", // Use expression body for operators
            "IDE0024", // Use expression body for operators
            "IDE0025", // Use expression body for properties
            "IDE0026", // Use expression body for indexers
            "IDE0027", // Use expression body for accessors
            "IDE0028", // Use collection initializers
            "IDE0029", // Use coalesce expression
            "IDE0030", // Use coalesce expression
            "IDE0031", // Use null propagation
            "IDE0032", // Use auto property
            "IDE0033", // Use explicitly provided tuple name
            "IDE0034", // Simplify default expression
            "IDE0035", // Remove unreachable code
            "IDE0036", // Order modifiers
            "IDE0037", // Use inferred member name
            "IDE0039", // Use local function
            "IDE0040", // Add accessibility modifiers
            "IDE0041", // Use is null check
            "IDE0042", // Deconstruct variable declaration
            "IDE0043", // Invalid format string
            "IDE0044", // Add readonly modifier
            "IDE0045", // Use conditional expression for assignment
            "IDE0046", // Use conditional expression for return
            "IDE0047", // Remove unnecessary parentheses
            "IDE0048", // Add parentheses for clarity
            "IDE0049", // Use language keywords instead of framework type names
            "IDE0050", // Convert to tuple
            "IDE0051", // Remove unused private member
            "IDE0052", // Remove unread private member
            "IDE0053", // Use expression body for lambdas
            "IDE0054", // Use compound assignment
            "IDE0055", // Fix formatting
            "IDE0056", // Use index operator
            "IDE0057", // Use range operator
            "IDE0058", // Remove unnecessary expression value
            "IDE0059", // Remove unnecessary assignment
            "IDE0060", // Remove unused parameter
            "IDE0061", // Use expression body for local functions
            "IDE0062", // Make local function static
            "IDE0063", // Use simple using statement
            "IDE0064", // Make struct fields writable
            "IDE0065", // using directive placement
            "IDE0066", // Use switch expression
            "IDE0070", // Use System.HashCode.Combine
            "IDE0071", // Simplify interpolation
            "IDE0072", // Add missing cases to switch expression
            "IDE0073", // Use file header
            "IDE0074", // Use coalesce compound assignment
            "IDE0075", // Simplify conditional expression
            "IDE0078", // Use pattern matching
            "IDE0079", // Remove unnecessary suppression
            "IDE0080", // Remove unnecessary suppression operator
            "IDE0081", // Remove ByVal
            "IDE0082", // Convert typeof to nameof
            "IDE0083", // Use pattern matching
            "IDE0084", // Use pattern matching (IsNot operator)
            "IDE0090", // Simplify new expression
            "IDE0100", // Remove unnecessary equality operator
            "IDE0110", // Remove unnecessary discard
            "IDE0120", // Simplify LINQ expression
            "IDE0130", // Namespace does not match folder structure
            "IDE0140", // Simplify object creation
            "IDE0150", // Prefer null check over type check
            "IDE0160", // Use block-scoped namespace
            "IDE0161", // Use file-scoped namespace
            "IDE0170", // Simplify property pattern
            "IDE0180", // Use tuple swap
            "IDE0200", // Remove unnecessary lambda expression
            "IDE0210", // Use top-level statements
            "IDE0211", // Use program main
            "IDE0220", // foreach cast
            "IDE0230", // Use UTF-8 string literal
            "IDE0240", // Nullable directive is redundant
            "IDE0241", // Nullable directive is unnecessary
            "IDE0250", // Struct can be made readonly
            "IDE0251", // Member can be made readonly
            "IDE0260", // Use pattern matching
            "IDE0270", // Null check can be simplified
            "IDE0280", // Use primary constructor
            "IDE0290", // Use primary constructor
            "IDE0300", // Use collection expression for array
            "IDE0301", // Use collection expression for empty
            "IDE0302", // Use collection expression for stackalloc
            "IDE0303", // Use collection expression for Create()
            "IDE0304", // Use collection expression for builder
            "IDE0305", // Use collection expression for fluent
            "IDE1005", // Remove unnecessary import
            "IDE1006"  // Naming rule violation
        };

        return fixableIds.Contains(id);
    }
}