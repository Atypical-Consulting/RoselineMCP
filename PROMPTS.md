# AnalyzeSolution Tool - Prompt Examples

This document provides example prompts for using the AnalyzeSolution tool in your MCP server. These prompts demonstrate various use cases and filtering options.

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

## Future Enhancement Prompts (Not Yet Implemented)

These prompts demonstrate planned features from the ONE-PAGER:

- "Analyze the GitHub repository https://github.com/myorg/myapp.git on the main branch"
- "Clone and analyze https://github.com/myorg/myapp.git branch feature/new-api"
- "Analyze the solution and suggest which diagnostic IDs have available code fixes"