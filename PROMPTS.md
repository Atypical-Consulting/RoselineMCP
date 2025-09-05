# MCP Code Analysis Tools - Prompt Examples

This document provides example prompts for using the code analysis tools in your MCP server. These prompts demonstrate various use cases and filtering options for both AnalyzeSolution and ListDiagnostics tools.

---

# Part 1: AnalyzeSolution Tool

## Basic Analysis Prompts

### 1. Analyze a Solution by Path
```
Analyze the C# solution located at /Users/myproject/MyApp.sln
```

### 2. Analyze a Directory Containing a Solution
```
Analyze the solution in the directory /Users/myproject/src
```

### 3. Quick Analysis with Limited Results
```
Analyze /Users/myproject/MyApp.sln and show me only the top 20 diagnostics
```

## Filtered Analysis Prompts

### 4. Focus on Errors Only
```
Analyze the solution at /Users/myproject/MyApp.sln but only show me Error level diagnostics
```

### 5. Include Warnings and Errors
```
Analyze /Users/myproject/MyApp.sln with severity level Warning to see all warnings and errors
```

### 6. Exclude Test Projects
```
Analyze the solution at /Users/myproject/MyApp.sln but exclude any projects containing "Test" in their name
```

### 7. Focus on Core Projects Only
```
Analyze /Users/myproject/MyApp.sln and only include projects that contain "Core" or "Domain" in their names
```

## Advanced Filtering Prompts

### 8. Combined Filters for Production Code
```
Analyze the solution at /Users/myproject/MyApp.sln excluding Test projects, showing only Error and Warning severity levels, with a maximum of 50 diagnostics
```

### 9. Comprehensive Analysis with All Severities
```
Perform a comprehensive analysis of /Users/myproject/MyApp.sln including Hidden, Info, Warning, and Error diagnostics, returning up to 200 results
```

### 10. Targeted Analysis for Specific Components
```
Analyze the solution at /Users/myproject/Enterprise.sln focusing only on projects containing "API" in the name, excluding any "Mock" or "Stub" projects, with all severity levels
```

## Example Tool Invocations

Here are the actual tool calls that would be made for some of these prompts:

### Example 1: Basic Analysis
```json
{
  "tool": "AnalyzeSolution",
  "parameters": {
    "pathOrGit": "/Users/myproject/MyApp.sln"
  }
}
```

### Example 2: Filtered by Severity
```json
{
  "tool": "AnalyzeSolution",
  "parameters": {
    "pathOrGit": "/Users/myproject/MyApp.sln",
    "severity": "Warning",
    "maxDiagnostics": 50
  }
}
```

### Example 3: Project Filtering
```json
{
  "tool": "AnalyzeSolution",
  "parameters": {
    "pathOrGit": "/Users/myproject/MyApp.sln",
    "include": "Core",
    "exclude": "Test",
    "severity": "Error",
    "maxDiagnostics": 100
  }
}
```

## Expected Response Format

The tool returns a JSON response with the following structure:

```json
{
  "solution": "MyApp.sln",
  "projects": 12,
  "diagnosticSummary": {
    "error": 3,
    "warning": 148,
    "info": 22,
    "hidden": 5
  },
  "topDiagnostics": [
    {
      "project": "MyApp.Core",
      "file": "Services/UserService.cs",
      "line": 87,
      "column": 12,
      "id": "CS0168",
      "severity": "warning",
      "message": "The variable 'ex' is declared but never used"
    }
  ]
}
```

## Tips for Effective Analysis

1. **Start Broad, Then Narrow**: Begin with a full analysis, then use filters to focus on specific issues
2. **Use Severity Filters**: Focus on errors first, then warnings, to prioritize critical issues
3. **Exclude Test Projects**: When analyzing production code quality, exclude test projects to reduce noise
4. **Adjust Max Diagnostics**: Use lower limits for quick overviews, higher limits for comprehensive analysis
5. **Combine Filters**: Use multiple filters together for precise, targeted analysis

---

# Part 2: ListDiagnostics Tool

## Basic Project Analysis Prompts

### 1. Analyze a Specific Project
```
List all diagnostics for the MyApp.Core project
```

### 2. Analyze by Project Path
```
Show diagnostics for the project at /Users/myproject/src/MyApp.Core/MyApp.Core.csproj
```

### 3. Quick Overview with Statistics
```
Give me a diagnostic summary for the MyApp.API project with statistics
```

## Filtered Diagnostics Prompts

### 4. Filter by Specific Diagnostic IDs
```
List all CS0168 and CS0219 diagnostics in the MyApp.Core project
```

### 5. Filter by File Patterns
```
Show me all diagnostics in Controller files in the MyApp.API project
```

### 6. Combined ID and File Filtering
```
Find RCS1213 and SA1101 diagnostics in Service.cs files within MyApp.Core
```

### 7. Focus on Fixable Issues
```
List diagnostics for MyApp.Core and highlight which ones can be automatically fixed
```

## Advanced Analysis Prompts

### 8. Comprehensive Project Analysis
```
Analyze MyApp.Core project showing all diagnostics with full statistics, grouped by severity and ID, limiting to 200 results
```

### 9. Targeted Code Quality Check
```
List all StyleCop analyzer warnings (SA* diagnostics) in the MyApp.API project
```

### 10. Performance and Code Style Issues
```
Show me all IDE* and RCS* diagnostics in MyApp.Core that affect code performance or style
```

## Example Tool Invocations

### Example 1: Basic Project Analysis
```json
{
  "tool": "ListDiagnostics",
  "parameters": {
    "project": "MyApp.Core"
  }
}
```

### Example 2: Filtered by Diagnostic IDs
```json
{
  "tool": "ListDiagnostics",
  "parameters": {
    "project": "MyApp.Core",
    "ids": ["CS0168", "CS0219", "RCS1213"],
    "max": 50
  }
}
```

### Example 3: File Pattern Filtering
```json
{
  "tool": "ListDiagnostics",
  "parameters": {
    "project": "MyApp.API",
    "files": ["Controller.cs", "Service.cs"],
    "max": 100
  }
}
```

### Example 4: Combined Filtering
```json
{
  "tool": "ListDiagnostics",
  "parameters": {
    "project": "/Users/myproject/src/MyApp.Core/MyApp.Core.csproj",
    "ids": ["SA1101", "SA1200", "IDE0005"],
    "files": ["Services/"],
    "max": 75
  }
}
```

## Expected Response Format

The ListDiagnostics tool returns:

```json
{
  "project": "MyApp.Core",
  "totalDiagnostics": 234,
  "diagnostics": [
    {
      "project": "MyApp.Core",
      "file": "Services/UserService.cs",
      "line": 45,
      "column": 8,
      "id": "RCS1213",
      "severity": "warning",
      "message": "Remove unused member declaration"
    }
  ],
  "stats": {
    "byId": {
      "RCS1213": 12,
      "CS0168": 5,
      "SA1101": 23
    },
    "bySeverity": {
      "Error": 3,
      "Warning": 178,
      "Info": 53
    }
  },
  "suggestedFixableIds": [
    "CS0168",
    "IDE0005",
    "RCS1213",
    "SA1101"
  ]
}
```

## Tips for Using ListDiagnostics

1. **Project Resolution**: The tool can find projects by name, full path to .csproj, or by searching in the solution
2. **ID Filtering**: Use diagnostic ID arrays to focus on specific code issues (e.g., unused variables, formatting)
3. **File Filtering**: Use file patterns to analyze specific components (Controllers, Services, Models)
4. **Fixable Diagnostics**: Check the `suggestedFixableIds` to know which issues can be automatically resolved
5. **Statistics Analysis**: Use the `stats` section to understand the distribution of issues

## Common Diagnostic ID Patterns

- **CS***: C# compiler warnings and errors
- **IDE***: Visual Studio IDE suggestions and refactorings
- **RCS***: Roslynator analyzer diagnostics
- **SA***: StyleCop analyzer rules
- **CA***: Code Analysis rules

## Workflow Examples

### Workflow 1: Progressive Code Cleanup
1. First, use AnalyzeSolution to get an overview
2. Then, use ListDiagnostics on problematic projects
3. Focus on fixable IDs for automated cleanup
4. Apply fixes (when ApplyFixes is implemented)

### Workflow 2: Targeted Quality Check
1. Use ListDiagnostics with specific ID filters (e.g., all SA* rules)
2. Review statistics to identify most common issues
3. Focus on files with highest diagnostic counts
4. Create fix plan based on severity and fixability

---

# Part 3: ApplyFixes Tool

## Basic Fix Application Prompts

### 1. Apply Single Diagnostic Fix
```
Apply fixes for CS0168 (unused variables) in the MyApp.Core project
```

### 2. Preview Changes Before Applying
```
Show me what would change if I fix all RCS1213 diagnostics in MyApp.API project without actually applying the changes
```

### 3. Fix Multiple Diagnostic Types
```
Fix both CS0168 and CS0219 diagnostics in the MyApp.Core project
```

## Targeted Code Cleanup Prompts

### 4. Clean Up Unused Code
```
Remove all unused variables and parameters (CS0168, CS0219, IDE0060) from MyApp.Services project
```

### 5. Fix Code Style Issues
```
Apply fixes for all SA1101 (this qualifier) and SA1200 (using directives) issues in MyApp.Core
```

### 6. Apply Roslynator Fixes
```
Fix all RCS1213 (remove unused member) and RCS1036 (remove redundant empty line) in the MyApp.API project
```

### 7. IDE Suggestions
```
Apply all IDE0005 (remove unnecessary imports) and IDE0051 (remove unused private members) fixes in MyApp.Core
```

## Advanced Fix Scenarios

### 8. Preview Complex Refactoring
```
Preview all fixes for IDE0017 (object initializers), IDE0028 (collection initializers), and IDE0031 (null propagation) in MyApp.Core without applying
```

### 9. Comprehensive Code Cleanup
```
Apply fixes for CS0168, CS0219, IDE0005, RCS1213, and SA1101 in the MyApp.Services project
```

### 10. Selective Pattern-Based Fixes
```
Fix all formatting and style issues (SA1000-SA1099 range) in MyApp.Core project, preview mode first
```

## Example Tool Invocations

### Example 1: Basic Fix Application
```json
{
  "tool": "ApplyFixes",
  "parameters": {
    "project": "MyApp.Core",
    "ids": ["CS0168"],
    "previewOnly": false
  }
}
```

### Example 2: Preview Mode
```json
{
  "tool": "ApplyFixes",
  "parameters": {
    "project": "MyApp.API",
    "ids": ["RCS1213", "IDE0051"],
    "previewOnly": true
  }
}
```

### Example 3: Multiple Fixes
```json
{
  "tool": "ApplyFixes",
  "parameters": {
    "project": "/Users/myproject/src/MyApp.Core/MyApp.Core.csproj",
    "ids": ["CS0168", "CS0219", "IDE0005", "SA1101"],
    "previewOnly": false
  }
}
```

### Example 4: Style and Formatting Fixes
```json
{
  "tool": "ApplyFixes",
  "parameters": {
    "project": "MyApp.Services",
    "ids": ["SA1101", "SA1200", "SA1210", "IDE0055"],
    "previewOnly": true
  }
}
```

## Expected Response Format

The ApplyFixes tool returns:

```json
{
  "project": "MyApp.Core",
  "fixersApplied": [
    "CS0168",
    "RCS1213"
  ],
  "changedFiles": [
    "Services/UserService.cs",
    "Models/Product.cs",
    "Controllers/ApiController.cs"
  ],
  "patch": "--- a/Services/UserService.cs\n+++ b/Services/UserService.cs\n@@ -45,7 +45,6 @@\n     public async Task<User> GetUser(int id)\n     {\n-        var unused = \"temp\";\n         return await _repository.GetByIdAsync(id);\n     }\n",
  "notes": [
    "Applied 5 fixes to 3 files"
  ],
  "fixedCount": 5,
  "previewOnly": false
}
```

## Tips for Using ApplyFixes

1. **Always Preview First**: Use `previewOnly: true` to review changes before applying
2. **Start Small**: Begin with a single diagnostic ID to understand the impact
3. **Group Related Fixes**: Apply similar fixes together (e.g., all unused code removals)
4. **Review Patches**: Examine the unified diff to ensure changes are correct
5. **Test After Fixing**: Run your tests after applying fixes to ensure nothing broke

## Common Fix Patterns

### Unused Code Removal
- **CS0168**: Variable declared but never used
- **CS0219**: Variable assigned but never used
- **CS0414**: Field assigned but never used
- **IDE0051**: Remove unused private member
- **IDE0052**: Remove unread private member
- **RCS1213**: Remove unused member declaration

### Code Style and Formatting
- **SA1101**: Prefix local calls with this
- **SA1200**: Using directives must be placed correctly
- **SA1210**: Using directives must be ordered alphabetically
- **IDE0055**: Fix formatting
- **IDE0005**: Remove unnecessary imports

### Code Modernization
- **IDE0017**: Use object initializers
- **IDE0028**: Use collection initializers
- **IDE0031**: Use null propagation
- **IDE0041**: Use is null check
- **IDE0066**: Use switch expression
- **IDE0090**: Simplify new expression

## Workflow Examples

### Workflow 1: Safe Code Cleanup
1. Use ListDiagnostics to identify fixable issues
2. Preview fixes with `previewOnly: true`
3. Review the patch output
4. Apply fixes if satisfied
5. Run tests to verify

### Workflow 2: Progressive Refactoring
1. Start with critical fixes (errors)
2. Move to warnings (unused code)
3. Apply style fixes (formatting)
4. Finally, apply modernization suggestions

### Workflow 3: Pull Request Preparation
1. Analyze solution to find all issues
2. List diagnostics for each project
3. Apply fixes in preview mode
4. Review all patches
5. Apply fixes and create PR with the changes

## Safety Considerations

- **Preview Mode**: Always available to review changes without risk
- **Atomic Operations**: All fixes for a diagnostic ID are applied together
- **Workspace Isolation**: Changes are made in a temporary workspace first
- **Patch Generation**: All changes are captured in a unified diff for review
- **Deterministic**: Same inputs produce same outputs

## Integration with Other Tools

### Complete Analysis Workflow
1. **AnalyzeSolution** - Get overview of all issues
2. **ListDiagnostics** - Detailed view of specific project issues
3. **ApplyFixes** - Fix selected issues with preview
4. **CreatePatch** (future) - Generate patches for review

---

# Part 4: CreatePatch Tool

## Basic Patch Generation Prompts

### 1. Simple Text Diff
```
Create a patch showing the differences between "Hello World" and "Hello Universe"
```

### 2. Code Changes Patch
```
Generate a unified diff between the original function implementation and the optimized version
```

### 3. Configuration File Changes
```
Create a patch file for the changes made to appsettings.json
```

## Code Comparison Prompts

### 4. Method Refactoring Diff
```
Show me a patch for the refactored GetUserById method with the async version
```

### 5. Class Structure Changes
```
Generate a diff showing the changes from the old Product class to the new one with additional properties
```

### 6. Bug Fix Documentation
```
Create a patch that shows what was changed to fix the null reference exception in UserService
```

### 7. API Endpoint Modifications
```
Generate a unified diff for the updated REST endpoint implementation
```

## Advanced Patch Scenarios

### 8. Multi-line Complex Changes
```
Create a patch showing the complete refactoring of the OrderProcessor class including new methods and removed code
```

### 9. Documentation Updates
```
Generate a diff patch for the README.md changes including the new installation instructions
```

### 10. Configuration Migration
```
Create a patch showing the migration from XML configuration to JSON configuration
```

## Example Tool Invocations

### Example 1: Simple Text Diff
```json
{
  "tool": "CreatePatch",
  "parameters": {
    "before": "public void ProcessOrder(Order order)\n{\n    // Process order\n    Console.WriteLine(\"Processing\");\n}",
    "after": "public async Task ProcessOrderAsync(Order order)\n{\n    // Process order asynchronously\n    await Task.Delay(100);\n    Console.WriteLine(\"Processing\");\n}",
    "fileName": "OrderService.cs"
  }
}
```

### Example 2: Configuration Changes
```json
{
  "tool": "CreatePatch",
  "parameters": {
    "before": "{\n  \"Logging\": {\n    \"LogLevel\": {\n      \"Default\": \"Information\"\n    }\n  }\n}",
    "after": "{\n  \"Logging\": {\n    \"LogLevel\": {\n      \"Default\": \"Warning\",\n      \"Microsoft\": \"Information\"\n    }\n  },\n  \"AllowedHosts\": \"*\"\n}",
    "fileName": "appsettings.json"
  }
}
```

### Example 3: Bug Fix Patch
```json
{
  "tool": "CreatePatch",
  "parameters": {
    "before": "public User GetUser(int id)\n{\n    return _users.First(u => u.Id == id);\n}",
    "after": "public User? GetUser(int id)\n{\n    return _users.FirstOrDefault(u => u.Id == id);\n}",
    "fileName": "UserRepository.cs"
  }
}
```

### Example 4: Class Refactoring
```json
{
  "tool": "CreatePatch",
  "parameters": {
    "before": "public class Product\n{\n    public int Id { get; set; }\n    public string Name { get; set; }\n}",
    "after": "public class Product\n{\n    public int Id { get; set; }\n    public string Name { get; set; } = string.Empty;\n    public decimal Price { get; set; }\n    public DateTime CreatedAt { get; set; }\n}",
    "fileName": "Product.cs"
  }
}
```

## Expected Response Format

The CreatePatch tool returns:

```json
{
  "patch": "--- a/UserService.cs\n+++ b/UserService.cs\n@@ -1,4 +1,5 @@\n public User GetUser(int id)\n {\n-    return _users.First(u => u.Id == id);\n+    // Added null safety\n+    return _users.FirstOrDefault(u => u.Id == id);\n }",
  "hasChanges": true,
  "linesAdded": 2,
  "linesRemoved": 1,
  "fileName": "UserService.cs",
  "summary": "UserService.cs: +2, -1 lines"
}
```

## Tips for Using CreatePatch

1. **Always Provide Context**: Include enough surrounding code for meaningful patches
2. **Use Descriptive File Names**: Helps identify what the patch modifies
3. **Preserve Formatting**: Maintain consistent indentation in before/after text
4. **Review Line Counts**: Check added/removed lines to verify changes
5. **Test Patch Application**: Ensure patches can be applied cleanly

## Common Use Cases

### Code Review Preparation
- Generate patches for proposed changes
- Document bug fixes with clear diffs
- Show refactoring improvements
- Demonstrate optimization changes

### Documentation
- Track configuration changes
- Document API modifications
- Show migration steps
- Capture setup changes

### Collaboration
- Share code changes without full files
- Provide focused feedback on specific changes
- Create reviewable change sets
- Document troubleshooting steps

## Integration Workflows

### Workflow 1: Fix and Document
1. Identify issue with AnalyzeSolution
2. Apply fixes with ApplyFixes
3. Extract specific changes
4. Use CreatePatch to document the fix
5. Share patch for review

### Workflow 2: Manual Refactoring Documentation
1. Copy original code (before)
2. Make manual improvements
3. Copy improved code (after)
4. Generate patch with CreatePatch
5. Include in pull request description

### Workflow 3: Configuration Migration
1. Save current configuration
2. Update to new format
3. Create patch showing migration
4. Use as template for other projects
5. Document in migration guide

## Patch Format Details

The tool generates standard unified diff format:
- `---` indicates the original file
- `+++` indicates the modified file
- `@@` marks the beginning of a change hunk
- `-` prefix for removed lines
- `+` prefix for added lines
- ` ` (space) prefix for unchanged context lines

## Advanced Features

### Multi-file Patches (Future)
While the current tool handles single file diffs, you can:
1. Generate multiple patches
2. Concatenate them manually
3. Apply as a series

### Patch Validation
The response includes:
- `hasChanges`: Quickly check if files differ
- `linesAdded`/`linesRemoved`: Verify scope of changes
- `summary`: Human-readable change description

### Best Practices
- Keep patches focused and atomic
- Include 3 lines of context by default
- Use meaningful file names
- Document why changes were made
- Test patches before sharing

---

## Future Enhancement Prompts (Not Yet Implemented)

These prompts demonstrate planned features from the ONE-PAGER:

- "Analyze the GitHub repository https://github.com/myorg/myapp.git on the main branch"
- "Apply fixes for all fixable issues across the entire solution"
- "Create a pull request with all StyleCop fixes"
- "Generate separate patches for each diagnostic category"
- "Apply this patch file to the current project"
- "Create a patch series for all changes in the feature branch"