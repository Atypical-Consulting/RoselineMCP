# MCP Code Analysis Tools - Usage Examples and Prompts

This document provides comprehensive examples and usage patterns for the RoselineMCP code analysis tools. These examples demonstrate real-world scenarios and best practices for analyzing and fixing C# code.

> **A note on diagnostic IDs used below:** `CS*` (Roslyn compiler), `IDE*` (.NET analyzers), and
> `RCS*` (Roslynator) diagnostics work out of the box — RoselineMCP bundles the Roslynator
> analyzers/fixers and executes them by default (`RoselineMCP:RunAnalyzers`). `SA*` (StyleCop)
> examples are included because they're a common real-world need, but
> **RoselineMCP itself does not bundle `StyleCop.Analyzers`** — those examples only produce
> results if the *solution you point RoselineMCP at* has `StyleCop.Analyzers` in its own
> `.csproj` (reported via the project's own analyzer references; auto-fixing additionally needs a
> loadable fixer). See [Supported Analyzers](README.md#supported-analyzers) in the README.

## Quick Start Examples

### Basic Solution Analysis
```
Analyze the C# solution at /Users/dev/MyProject/MyProject.sln
```

### Project-Specific Diagnostics
```
List all diagnostics for the MyApp.Core project
```

### Apply Automated Fixes
```
Fix all unused variable warnings (CS0168) in MyApp.Core project
```

### Generate a Patch
```
Create a patch showing the differences between the original and refactored code
```

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

## Real-World Scenarios

### Pre-Release Code Quality Check
```
Analyze our production solution at /src/Production.sln, excluding test and mock projects, focusing on Error and Warning severities only, limited to 100 most critical issues
```

### Technical Debt Assessment
```
Perform a full analysis of /legacy/LegacyApp.sln including all severity levels to assess technical debt, returning up to 500 diagnostics
```

### API Surface Review
```
Analyze /src/PublicAPI.sln focusing only on projects with "Public" or "API" in the name to review our public API surface for issues
```

## Example Tool Invocations

### Basic Analysis
```json
{
  "tool": "AnalyzeSolution",
  "parameters": {
    "pathOrGit": "/Users/myproject/MyApp.sln"
  }
}
```

### Filtered by Severity
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

### Project Filtering
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

## Advanced Analysis Prompts

### 7. Focus on Fixable Issues
```
List diagnostics for MyApp.Core and highlight which ones can be automatically fixed
```

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

## Real-World Scenarios

### Code Review Preparation
```
List all fixable diagnostics in MyApp.Core so we can clean up the code before review
```

### Security Audit
```
Show me all security-related diagnostics (CA2100, CA2213, CA3075) in our MyApp.Security project
```

### Performance Analysis
```
List performance-related diagnostics (CA1806, CA1810-CA1824) in the MyApp.Performance project
```

## Example Tool Invocations

### Basic Project Analysis
```json
{
  "tool": "ListDiagnostics",
  "parameters": {
    "project": "MyApp.Core"
  }
}
```

### Filtered by Diagnostic IDs
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

### File Pattern Filtering

`files` is a case-insensitive **substring** match against each diagnostic's file path — not a
glob pattern:

```json
{
  "tool": "ListDiagnostics",
  "parameters": {
    "project": "MyApp.API",
    "files": ["Controllers", "Services"],
    "max": 100
  }
}
```

## Expected Response Format

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

## Advanced Fix Scenarios

### 7. IDE Suggestions
```
Apply all IDE0005 (remove unnecessary imports) and IDE0051 (remove unused private members) fixes in MyApp.Core
```

### 8. Preview Complex Refactoring
```
Preview all fixes for IDE0017 (object initializers), IDE0028 (collection initializers), and IDE0031 (null propagation) in MyApp.Core without applying
```

### 9. Comprehensive Code Cleanup
```
Apply fixes for CS0168, CS0219, IDE0005, RCS1213, and SA1101 in the MyApp.Services project
```

## Real-World Scenarios

### Pre-Commit Cleanup
```
Fix all unused code warnings (CS0168, CS0219, IDE0051) in MyApp.Core before committing
```

### Style Conformance
```
Apply all StyleCop fixes (SA1101, SA1200, SA1210) to ensure our MyApp.API project meets style guidelines
```

### Modernization Pass
```
Preview modernization fixes (IDE0017, IDE0028, IDE0090) for MyApp.Legacy to see upgrade opportunities
```

## Example Tool Invocations

### Basic Fix Application
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

### Preview Mode
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

### Multiple Fixes
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

## Expected Response Format

```json
{
  "project": "MyApp.Core",
  "fixesApplied": 12,
  "filesChanged": [
    "Services/UserService.cs",
    "Models/Product.cs",
    "Controllers/ApiController.cs"
  ],
  "patch": "--- a/Services/UserService.cs\n+++ b/Services/UserService.cs\n@@ -45,7 +45,6 @@\n     public async Task<User> GetUser(int id)\n     {\n-        var unused = \"temp\";\n         return await _repository.GetByIdAsync(id);\n     }",
  "appliedFixers": [
    {
      "diagnosticId": "CS0168",
      "fixerName": "Remove Unused Variable",
      "count": 5
    },
    {
      "diagnosticId": "RCS1213",
      "fixerName": "Remove Unused Member Declaration",
      "count": 7
    }
  ]
}
```

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

## Real-World Scenarios

### API Migration Documentation
```
Create a patch showing the migration from REST to GraphQL endpoint implementation
```

### Security Fix Documentation
```
Generate a diff showing the SQL injection vulnerability fix in the database layer
```

### Performance Optimization
```
Create a patch documenting the performance improvements made to the data processing algorithm
```

## Example Tool Invocations

### Simple Text Diff
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

### Bug Fix Patch
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

## Expected Response Format

```json
{
  "patch": "--- a/UserService.cs\n+++ b/UserService.cs\n@@ -1,4 +1,5 @@\n public User GetUser(int id)\n {\n-    return _users.First(u => u.Id == id);\n+    // Added null safety\n+    return _users.FirstOrDefault(u => u.Id == id);\n }",
  "hasChanges": true,
  "linesAdded": 2,
  "linesRemoved": 1
}
```

---

# Complete Workflow Examples

## Workflow 1: Code Quality Improvement

### Step 1: Analyze the Solution
```
Analyze the solution at /src/MyApp.sln excluding test projects, focusing on warnings and errors
```

### Step 2: List Specific Project Issues
```
List all fixable diagnostics for the MyApp.Core project with statistics
```

### Step 3: Preview Fixes
```
Preview fixes for CS0168, CS0219, and IDE0005 in MyApp.Core project
```

### Step 4: Apply Fixes
```
Apply the fixes for CS0168, CS0219, and IDE0005 in MyApp.Core project
```

### Step 5: Document Changes
```
Create a patch showing all the changes made to MyApp.Core
```

## Workflow 2: Pre-Release Cleanup

### Step 1: Comprehensive Analysis
```
Perform a comprehensive analysis of /src/Production.sln showing all severity levels
```

### Step 2: Focus on Critical Issues
```
List all Error severity diagnostics across all projects in the solution
```

### Step 3: Fix Errors
```
Apply fixes for all fixable errors in the production projects
```

### Step 4: Style Compliance
```
Apply all StyleCop fixes (SA* diagnostics) to ensure code style compliance
```

### Step 5: Final Verification
```
Re-analyze the solution to verify all critical issues are resolved
```

## Workflow 3: Legacy Code Modernization

### Step 1: Baseline Assessment
```
Analyze /legacy/OldApp.sln including all diagnostics to establish baseline
```

### Step 2: Identify Modernization Opportunities
```
List all IDE* diagnostics that represent modernization opportunities
```

### Step 3: Preview Modernizations
```
Preview all modernization fixes (IDE0017, IDE0028, IDE0031, IDE0090) to assess impact
```

### Step 4: Incremental Application
```
Apply modernization fixes project by project, starting with the core library
```

### Step 5: Track Progress
```
Generate patches for each modernization phase for documentation
```

---

# Tips and Best Practices

## General Guidelines

1. **Start with Analysis**: Always analyze first to understand the scope
2. **Use Preview Mode**: Preview fixes before applying them
3. **Filter Strategically**: Use filters to focus on relevant issues
4. **Document Changes**: Generate patches for significant changes
5. **Test After Fixes**: Always run tests after applying fixes

## Performance Tips

1. **Limit Results**: Use `maxDiagnostics` to avoid overwhelming output
2. **Target Specific Projects**: Analyze projects individually for large solutions
3. **Filter by Severity**: Start with errors, then warnings, then suggestions
4. **Batch Similar Fixes**: Group related diagnostic IDs together

## Safety Practices

1. **Version Control**: Ensure code is committed before applying fixes
2. **Preview First**: Always preview complex or numerous fixes
3. **Incremental Fixes**: Apply fixes in small batches
4. **Review Patches**: Examine generated patches before committing
5. **Backup Critical Code**: Keep backups when working on production code

## Common Diagnostic Categories

### Compiler Warnings (CS*)
- CS0168: Variable declared but never used
- CS0219: Variable assigned but never used
- CS0649: Field never assigned
- CS1591: Missing XML documentation

### IDE Suggestions (IDE*)
- IDE0005: Remove unnecessary imports
- IDE0017: Use object initializers
- IDE0031: Use null propagation
- IDE0060: Remove unused parameter

### Roslynator (RCS*)
- RCS1001: Add braces
- RCS1036: Remove redundant empty line
- RCS1213: Remove unused member
- RCS1018: Add accessibility modifiers

### StyleCop (SA*) — requires `StyleCop.Analyzers` in the *target* solution, not bundled with RoselineMCP
- SA1101: Prefix local calls with this
- SA1200: Using directives placement
- SA1210: Using directives ordering
- SA1600: Elements should be documented

---

# Troubleshooting

## Common Issues and Solutions

### No Diagnostics Found
- Verify the project/solution path is correct
- Check if analyzers are installed in the project
- Ensure the project builds successfully

### Fixes Not Applied
- Verify the diagnostic ID is fixable
- Check if the fix provider is available
- Ensure files are not read-only

### Large Output
- Use `maxDiagnostics` parameter to limit results
- Apply filters to focus on specific issues
- Process projects individually

### Performance Issues
- Reduce scope with include/exclude patterns
- Process smaller batches of fixes
- Use preview mode for large operations