using System.Reflection;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests using AdhocWorkspace to cover solution-level methods in SolutionAnalyzerService.
/// </summary>
public class SolutionAnalyzerServiceAdhocTests
{
    private readonly SolutionAnalyzerService _sut;
    private readonly DiagnosticFilterService _realFilterService;

    public SolutionAnalyzerServiceAdhocTests()
    {
        var logger = A.Fake<ILogger<SolutionAnalyzerService>>();
        var msBuildService = A.Fake<IMSBuildService>();
        _realFilterService = new DiagnosticFilterService();
        _sut = new SolutionAnalyzerService(logger, msBuildService, _realFilterService);
    }

    private T InvokePrivate<T>(string methodName, params object?[] args)
    {
        // Handle overloaded methods - try to find by name and param count
        var methods = typeof(SolutionAnalyzerService)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == methodName && m.GetParameters().Length == args.Length)
            .ToList();
        
        var method = methods.FirstOrDefault();
        method.ShouldNotBeNull($"Method '{methodName}' with {args.Length} params not found");
        return (T)method!.Invoke(_sut, args)!;
    }

    private static (AdhocWorkspace Workspace, Solution Solution) CreateSolutionWithProjects(
        params string[] projectNames)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var versionStamp = VersionStamp.Create();

        foreach (var name in projectNames)
        {
            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                projectId, versionStamp, name, name, LanguageNames.CSharp,
                filePath: $"/projects/{name}/{name}.csproj");
            solution = solution.AddProject(projectInfo);
        }

        return (workspace, solution);
    }

    public class FindProjectInSolutionTests : SolutionAnalyzerServiceAdhocTests
    {
        [Fact]
        public void Should_Find_Project_By_Exact_Name()
        {
            // Arrange
            var (_, solution) = CreateSolutionWithProjects("MyService", "MyApi", "MyLib");

            // Act
            var result = InvokePrivate<Project?>("FindProjectInSolution", solution, "MyService");

            // Assert
            result.ShouldNotBeNull();
            result!.Name.ShouldBe("MyService");
        }

        [Fact]
        public void Should_Find_Project_Case_Insensitive()
        {
            // Arrange
            var (_, solution) = CreateSolutionWithProjects("MyService");

            // Act
            var result = InvokePrivate<Project?>("FindProjectInSolution", solution, "myservice");

            // Assert
            result.ShouldNotBeNull();
        }

        [Fact]
        public void Should_Return_Null_When_Project_Not_Found()
        {
            // Arrange
            var (_, solution) = CreateSolutionWithProjects("ProjectA", "ProjectB");

            // Act
            var result = InvokePrivate<Project?>("FindProjectInSolution", solution, "NonExistentProject");

            // Assert
            result.ShouldBeNull();
        }

        [Fact]
        public void Should_Return_Null_For_Empty_Solution()
        {
            // Arrange
            var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;

            // Act
            var result = InvokePrivate<Project?>("FindProjectInSolution", solution, "AnyProject");

            // Assert
            result.ShouldBeNull();
        }

        [Fact]
        public void Should_Find_Project_By_File_Path_Contains()
        {
            // Arrange - project with known file path
            var (_, solution) = CreateSolutionWithProjects("MyApi");
            // The project was created with filePath="/projects/MyApi/MyApi.csproj"
            // It should be findable by "MyApi" in the path

            // Act
            var result = InvokePrivate<Project?>("FindProjectInSolution", solution, "MyApi");

            // Assert
            result.ShouldNotBeNull();
        }
    }

    public class BuildAnalyzeSolutionResponseTests : SolutionAnalyzerServiceAdhocTests
    {
        [Fact]
        public void Should_Build_Response_With_Solution_Name()
        {
            // Arrange
            var (_, solution) = CreateSolutionWithProjects("ProjectA", "ProjectB");
            var solutionPath = "/projects/MySolution.sln";
            var diagnostics = new List<DiagnosticDetail>
            {
                new() { Id = "CS0168", Severity = "warning", File = "a.cs", Line = 1 }
            };
            var summary = new DiagnosticSummary { Warning = 1 };

            // Act — invoke BuildAnalyzeSolutionResponse via reflection
            var methods = typeof(SolutionAnalyzerService)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "BuildAnalyzeSolutionResponse")
                .ToList();
            var method = methods.FirstOrDefault();
            method.ShouldNotBeNull();
            var result = (AnalyzeSolutionResponse)method!.Invoke(_sut,
                new object[] { solutionPath, solution, diagnostics, summary, 100 })!;

            // Assert
            result.ShouldNotBeNull();
            result.Solution.ShouldBe("MySolution.sln");
            result.Projects.ShouldBe(2);
            result.DiagnosticSummary.ShouldBe(summary);
        }

        [Fact]
        public void Should_Build_Response_With_Ordered_Diagnostics()
        {
            // Arrange
            var (_, solution) = CreateSolutionWithProjects("Project1");
            var solutionPath = "/Solution.sln";
            var diagnostics = new List<DiagnosticDetail>
            {
                new() { Id = "CS0001", Severity = "info", File = "a.cs", Line = 1 },
                new() { Id = "CS0002", Severity = "error", File = "a.cs", Line = 2 },
                new() { Id = "CS0003", Severity = "warning", File = "a.cs", Line = 3 },
            };
            var summary = new DiagnosticSummary();

            // Act
            var method = typeof(SolutionAnalyzerService)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .First(m => m.Name == "BuildAnalyzeSolutionResponse");
            var result = (AnalyzeSolutionResponse)method.Invoke(_sut,
                new object[] { solutionPath, solution, diagnostics, summary, 100 })!;

            // Assert — should be ordered by severity (error first)
            result.TopDiagnostics.ShouldNotBeNull();
            result.TopDiagnostics.Count.ShouldBe(3);
            result.TopDiagnostics[0].Severity.ShouldBe("error");
        }

        [Fact]
        public void Should_Limit_TopDiagnostics_By_MaxDiagnostics()
        {
            // Arrange
            var (_, solution) = CreateSolutionWithProjects("Project1");
            var diagnostics = Enumerable.Range(1, 10)
                .Select(i => new DiagnosticDetail { Id = $"CS{i:000}", Severity = "warning", File = "a.cs", Line = i })
                .ToList();

            // Act
            var method = typeof(SolutionAnalyzerService)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .First(m => m.Name == "BuildAnalyzeSolutionResponse");
            var result = (AnalyzeSolutionResponse)method.Invoke(_sut,
                new object[] { "/Solution.sln", solution, diagnostics, new DiagnosticSummary(), 3 })!;

            // Assert — limited to 3
            result.TopDiagnostics.Count.ShouldBe(3);
        }
    }
}
