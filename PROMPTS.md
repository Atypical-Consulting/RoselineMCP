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

## Future Enhancement Prompts (Not Yet Implemented)

These prompts demonstrate planned features from the ONE-PAGER:

- "Analyze the GitHub repository https://github.com/myorg/myapp.git on the main branch"
- "Apply fixes for all RCS1213 diagnostics in MyApp.Core"
- "Create a patch file with fixes for CS0168 and CS0219 in the entire solution"
- "Generate a unified diff for all fixable StyleCop violations"