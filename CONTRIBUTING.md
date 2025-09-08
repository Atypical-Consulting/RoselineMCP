# Contributing to RoselineMCP

Thank you for your interest in contributing to RoselineMCP! This document provides guidelines and instructions for contributing to the project.

## Code of Conduct

By participating in this project, you agree to abide by our Code of Conduct:
- Be respectful and inclusive
- Welcome newcomers and help them get started
- Focus on constructive criticism
- Accept feedback gracefully

## Getting Started

### Prerequisites

1. Install .NET 9.0 SDK or later
2. Install an IDE (Visual Studio, VS Code with C# extension, or JetBrains Rider)
3. Fork and clone the repository
4. Run `dotnet restore` to restore packages
5. Run `dotnet build` to verify the build works
6. Run `dotnet test` to ensure all tests pass

### Development Environment Setup

```bash
# Clone your fork
git clone https://github.com/YOUR_USERNAME/RoselineMCP.git
cd RoselineMCP

# Add upstream remote
git remote add upstream https://github.com/phmatray/RoselineMCP.git

# Install dependencies
dotnet restore

# Build the project
dotnet build

# Run tests
dotnet test
```

## How to Contribute

### Reporting Issues

1. Check existing issues to avoid duplicates
2. Use issue templates when available
3. Include:
   - Clear description of the problem
   - Steps to reproduce
   - Expected vs actual behavior
   - Environment details (.NET version, OS, etc.)
   - Relevant logs or error messages

### Suggesting Features

1. Open a discussion first for major features
2. Explain the use case and benefits
3. Consider implementation complexity
4. Be open to alternative approaches

### Submitting Pull Requests

1. **Create a branch** for your feature/fix:
   ```bash
   git checkout -b feature/your-feature-name
   ```

2. **Make your changes**:
   - Follow existing code style and conventions
   - Add/update tests as needed
   - Update documentation if applicable
   - Keep commits focused and atomic

3. **Test your changes**:
   ```bash
   # Run all tests
   dotnet test
   
   # Run specific test
   dotnet test --filter "FullyQualifiedName~YourTestName"
   
   # Check code coverage
   dotnet test --collect:"XPlat Code Coverage"
   ```

4. **Commit your changes**:
   ```bash
   git add .
   git commit -m "feat: add new diagnostic analyzer support"
   ```
   
   Follow commit message conventions:
   - `feat:` New feature
   - `fix:` Bug fix
   - `docs:` Documentation changes
   - `test:` Test additions/changes
   - `refactor:` Code refactoring
   - `perf:` Performance improvements
   - `chore:` Maintenance tasks

5. **Push and create PR**:
   ```bash
   git push origin feature/your-feature-name
   ```
   Then create a pull request on GitHub.

## Development Guidelines

### Code Style

- Follow C# coding conventions
- Use meaningful variable and method names
- Keep methods small and focused (single responsibility)
- Add XML documentation comments for public APIs
- Use async/await for asynchronous operations
- Prefer LINQ for collection operations where readable

### Architecture Guidelines

1. **Service Layer**:
   - All services must have an interface in `Interfaces/`
   - Implement services in `Services/`
   - Use dependency injection for service dependencies
   - Keep services focused on a single responsibility

2. **MCP Tools**:
   - Place tool implementations in `Tools/`
   - Use `[McpServerTool]` attribute
   - Add `[Description]` attributes for all parameters
   - Return JSON responses consistently
   - Handle errors gracefully with try-catch

3. **Models**:
   - Place DTOs in `Models/`
   - Keep models immutable where possible
   - Use record types for simple DTOs
   - Include XML documentation

### Testing Guidelines

1. **Unit Tests**:
   - Place tests in corresponding folders under `RoselineMCP.Tests/`
   - Name test classes with `Tests` suffix
   - Use descriptive test method names
   - Follow Arrange-Act-Assert pattern
   - Mock external dependencies

2. **Test Coverage**:
   - Aim for >80% code coverage
   - Test edge cases and error conditions
   - Include both positive and negative test cases

Example test structure:
```csharp
[Fact]
public async Task AnalyzeSolutionAsync_WithValidPath_ReturnsExpectedDiagnostics()
{
    // Arrange
    var service = CreateService();
    var solutionPath = "test.sln";
    
    // Act
    var result = await service.AnalyzeSolutionAsync(solutionPath);
    
    // Assert
    Assert.NotNull(result);
    Assert.Equal(expected, result.DiagnosticCount);
}
```

### Adding New MCP Tools

1. Create a new class in `Tools/` folder
2. Add the tool method with proper attributes:

```csharp
public static class MyNewTool
{
    [McpServerTool]
    [Description("Clear description of what the tool does")]
    public static async Task<string> ExecuteMyTool(
        IRequiredService service,
        [Description("Parameter description")] string param1,
        [Description("Optional parameter")] string? param2 = null)
    {
        try
        {
            // Implementation
            var result = await service.ProcessAsync(param1, param2);
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new 
            { 
                error = ex.Message, 
                type = ex.GetType().Name 
            });
        }
    }
}
```

3. Add corresponding service interface and implementation if needed
4. Write comprehensive tests
5. Update API documentation

### Documentation

- Update README.md for user-facing changes
- Update API.md for new tools or API changes
- Update ARCHITECTURE.md for structural changes
- Include XML comments for public methods
- Add examples for complex features

## Pull Request Process

1. **Before submitting**:
   - Ensure all tests pass
   - Update documentation
   - Rebase on latest main branch
   - Squash related commits if needed

2. **PR Description**:
   - Link related issues
   - Describe what changes were made
   - Explain why the changes are needed
   - Include screenshots for UI changes
   - List any breaking changes

3. **Review Process**:
   - Address reviewer feedback promptly
   - Be open to suggestions
   - Keep discussions focused and professional
   - Update PR based on feedback

4. **Merge Requirements**:
   - All CI checks must pass
   - At least one approving review
   - No unresolved conversations
   - Up-to-date with main branch

## Release Process

1. Version numbers follow Semantic Versioning (MAJOR.MINOR.PATCH)
2. Releases are created from the main branch
3. Each release includes:
   - Updated version numbers
   - Changelog with all changes
   - Updated documentation
   - Git tag

## Getting Help

- **Discord**: Join our development discussion
- **GitHub Issues**: For bug reports and features
- **GitHub Discussions**: For questions and ideas
- **Email**: Contact maintainers for sensitive issues

## Recognition

Contributors are recognized in:
- CONTRIBUTORS.md file
- Release notes
- Project documentation

Thank you for contributing to RoselineMCP!