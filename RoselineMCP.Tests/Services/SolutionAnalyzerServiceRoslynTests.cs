using System.Reflection;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for SolutionAnalyzerService that use AdhocWorkspace (in-memory Roslyn workspace)
/// to test the deep async paths that require real compilations.
/// AdhocWorkspace does NOT require MSBuild — it compiles C# in-memory using Roslyn directly.
/// </summary>
public class SolutionAnalyzerServiceRoslynTests
{
    private readonly SolutionAnalyzerService _sut;
    private readonly DiagnosticFilterService _realFilterService;

    public SolutionAnalyzerServiceRoslynTests()
    {
        var logger = A.Fake<ILogger<SolutionAnalyzerService>>();
        var msBuildService = A.Fake<IMSBuildService>();
        var codeFixProviderFactory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
        _realFilterService = new DiagnosticFilterService(codeFixProviderFactory);
        _sut = new SolutionAnalyzerService(logger, msBuildService, _realFilterService, A.Fake<IProjectLoader>());
    }

    #region Helpers

    private T InvokePrivate<T>(string methodName, params object?[] args)
    {
        var method = typeof(SolutionAnalyzerService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull($"Method '{methodName}' not found");
        return (T)method!.Invoke(_sut, args)!;
    }

    private async Task<T> InvokePrivateAsync<T>(string methodName, params object?[] args)
    {
        var method = typeof(SolutionAnalyzerService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull($"Method '{methodName}' not found");
        var task = (Task<T>)method!.Invoke(_sut, args)!;
        return await task;
    }

    private async Task InvokePrivateAsyncVoid(string methodName, params object?[] args)
    {
        var method = typeof(SolutionAnalyzerService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull($"Method '{methodName}' not found");
        await (Task)method!.Invoke(_sut, args)!;
    }

    /// <summary>
    /// Creates a real in-memory Roslyn project using AdhocWorkspace.
    /// The compilation from this project is real and produces actual diagnostics.
    /// </summary>
    private static (AdhocWorkspace Workspace, Project Project) CreateInMemoryProject(
        string projectName, string sourceCode)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var projectId = ProjectId.CreateNewId();
        var versionStamp = VersionStamp.Create();
        var projectInfo = ProjectInfo.Create(
            projectId, versionStamp, projectName, projectName, LanguageNames.CSharp);

        solution = solution.AddProject(projectInfo);
        var project = solution.GetProject(projectId)!;
        var document = project.AddDocument("Test.cs", SourceText.From(sourceCode));
        project = document.Project;

        return (workspace, project);
    }

    #endregion

    #region GetProjectCompilationAsync

    public class GetProjectCompilationAsyncTests : SolutionAnalyzerServiceRoslynTests
    {
        [Fact]
        public async Task Should_Return_Compilation_For_Valid_Project()
        {
            // Arrange
            var (_, project) = CreateInMemoryProject("TestProject", "class Foo { }");

            // Act
            var result = await InvokePrivateAsync<Compilation?>(
                "GetProjectCompilationAsync", project, CancellationToken.None);

            // Assert
            result.ShouldNotBeNull();
        }

        [Fact]
        public async Task Should_Return_Compilation_With_Diagnostics_For_Code_With_Issues()
        {
            // Arrange — unused variable produces CS0168
            var (_, project) = CreateInMemoryProject("TestProject",
                "class Foo { void Bar() { int unusedVar; } }");

            // Act
            var compilation = await InvokePrivateAsync<Compilation?>(
                "GetProjectCompilationAsync", project, CancellationToken.None);

            // Assert
            compilation.ShouldNotBeNull();
            // Compilation exists and may have diagnostics
        }
    }

    #endregion

    #region GetFilteredDiagnosticsAsync

    public class GetFilteredDiagnosticsTests : SolutionAnalyzerServiceRoslynTests
    {
        [Fact]
        public async Task Should_Return_Diagnostics_From_Real_Compilation()
        {
            // Arrange — code with an issue
            var (_, project) = CreateInMemoryProject("TestProject",
                "class Foo { void Bar() { int unusedVar; } }");

            var compilation = await project.GetCompilationAsync();
            compilation.ShouldNotBeNull();

            // Get the AnalysisContext type via reflection and create instance
            var contextType = typeof(SolutionAnalyzerService)
                .GetNestedType("AnalysisContext", BindingFlags.NonPublic);
            contextType.ShouldNotBeNull();
            var context = contextType!.GetConstructor(Type.EmptyTypes)!.Invoke(null);

            // Set MaxDiagnostics
            contextType.GetProperty("MaxDiagnostics")!.SetValue(context, 100);
            contextType.GetProperty("Severity")!.SetValue(context, (string?)null);

            // Act
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "GetFilteredDiagnosticsAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var result = (await (Task<(List<Diagnostic> Diagnostics, AnalyzerLoadReport AnalyzerLoad)>)method!.Invoke(_sut, new object[] { project, compilation!, context, CancellationToken.None })!).Diagnostics;

            // Assert
            result.ShouldNotBeNull();
        }

        [Fact]
        public async Task Should_Filter_Diagnostics_By_Severity()
        {
            // Arrange
            var (_, project) = CreateInMemoryProject("TestProject", "class Foo { }");
            var compilation = await project.GetCompilationAsync();
            compilation.ShouldNotBeNull();

            var contextType = typeof(SolutionAnalyzerService)
                .GetNestedType("AnalysisContext", BindingFlags.NonPublic)!;
            var context = contextType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
            contextType.GetProperty("MaxDiagnostics")!.SetValue(context, 100);
            contextType.GetProperty("Severity")!.SetValue(context, "Error"); // Only errors

            var method = typeof(SolutionAnalyzerService).GetMethod(
                "GetFilteredDiagnosticsAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = (await (Task<(List<Diagnostic> Diagnostics, AnalyzerLoadReport AnalyzerLoad)>)method.Invoke(_sut, new object[] { project, compilation!, context, CancellationToken.None })!).Diagnostics;

            // Assert — filter by "Error" should exclude warnings/info
            result.ShouldNotBeNull();
            result.ShouldAllBe(d => d.Severity >= DiagnosticSeverity.Error);
        }
    }

    #endregion

    #region ProcessProjectDiagnostics

    public class ProcessProjectDiagnosticsTests : SolutionAnalyzerServiceRoslynTests
    {
        private static (List<DiagnosticDetail> TopDiagnostics, DiagnosticSummary Summary) ReadResult(object result)
        {
            var resultType = result.GetType();
            var top = (List<DiagnosticDetail>)resultType.GetProperty("TopDiagnostics")!.GetValue(result)!;
            var summary = (DiagnosticSummary)resultType.GetProperty("Summary")!.GetValue(result)!;
            return (top, summary);
        }

        [Fact]
        public async Task Should_Process_Diagnostics_From_Real_Compilation()
        {
            // Arrange
            var (_, project) = CreateInMemoryProject("TestProject",
                "class Foo { void Bar() { int unusedVar; } }");
            var compilation = await project.GetCompilationAsync();
            var diagnostics = compilation!.GetDiagnostics().ToList();

            // Act
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "ProcessProjectDiagnostics",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = method.Invoke(_sut, new object[] { diagnostics, "TestProject", 100 })!;

            // Assert
            var (topDiagnostics, summary) = ReadResult(result);
            topDiagnostics.ShouldNotBeNull();
            summary.ShouldNotBeNull();
        }

        [Fact]
        public async Task Should_Respect_MaxDiagnostics_Limit_But_Count_All_Diagnostics()
        {
            // Arrange — create lots of diagnostics
            var code = string.Join("\n", Enumerable.Range(1, 20).Select(i =>
                $"class Foo{i} {{ void Bar() {{ int unused{i}; }} }}"));
            var (_, project) = CreateInMemoryProject("TestProject", code);
            var compilation = await project.GetCompilationAsync();
            var diagnostics = compilation!.GetDiagnostics().ToList();

            // Act — with max of 5
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "ProcessProjectDiagnostics",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = method.Invoke(_sut, new object[] { diagnostics, "TestProject", 5 })!;

            // Assert — details capped at max, but the summary counts every diagnostic
            var (topDiagnostics, summary) = ReadResult(result);
            topDiagnostics.Count.ShouldBeLessThanOrEqualTo(5);
            var totalCounted = summary.Error + summary.Warning + summary.Info + summary.Hidden;
            totalCounted.ShouldBe(diagnostics.Count);
        }
    }

    #endregion

    #region AnalyzeProjectAsync

    public class AnalyzeProjectAsyncTests : SolutionAnalyzerServiceRoslynTests
    {
        private async Task<(List<DiagnosticDetail> TopDiagnostics, DiagnosticSummary Summary)> AnalyzeAsync(
            Project project, object context)
        {
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "AnalyzeProjectAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var task = (Task)method.Invoke(_sut, new object[] { project, context, CancellationToken.None })!;
            await task;

            // Task<ProjectAnalysisResult> where the result type is private — unwrap via reflection.
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var resultType = result.GetType();
            var top = (List<DiagnosticDetail>)resultType.GetProperty("TopDiagnostics")!.GetValue(result)!;
            var summary = (DiagnosticSummary)resultType.GetProperty("Summary")!.GetValue(result)!;
            return (top, summary);
        }

        [Fact]
        public async Task Should_Analyze_Real_Project_With_AdhocWorkspace()
        {
            // Arrange — a simple C# class that can be compiled in-memory
            var (_, project) = CreateInMemoryProject("TestProject", "class Foo { }");

            // Need to create an AnalysisContext instance
            var contextType = typeof(SolutionAnalyzerService)
                .GetNestedType("AnalysisContext", BindingFlags.NonPublic)!;
            var context = contextType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
            contextType.GetProperty("MaxDiagnostics")!.SetValue(context, 100);

            // Act — call AnalyzeProjectAsync via reflection
            var (topDiagnostics, summary) = await AnalyzeAsync(project, context);

            // Assert — the project compiled successfully (no exceptions)
            topDiagnostics.ShouldNotBeNull();
            summary.ShouldNotBeNull();
        }

        [Fact]
        public async Task Should_Handle_Project_With_Diagnostics()
        {
            // Arrange — code with a warning (unused variable)
            var (_, project) = CreateInMemoryProject("TestProject",
                "class Foo { void Bar() { int unused; } }");

            var contextType = typeof(SolutionAnalyzerService)
                .GetNestedType("AnalysisContext", BindingFlags.NonPublic)!;
            var context = contextType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
            contextType.GetProperty("MaxDiagnostics")!.SetValue(context, 100);

            // Act
            var (topDiagnostics, _) = await AnalyzeAsync(project, context);

            // Assert — diagnostics from unused variable
            topDiagnostics.ShouldNotBeNull();
        }
    }

    #endregion

    #region AnalyzeProjectsAsync

    public class AnalyzeProjectsAsyncTests : SolutionAnalyzerServiceRoslynTests
    {
        [Fact]
        public async Task Should_Analyze_All_Projects_In_Solution()
        {
            // Arrange
            var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;

            // Add two projects
            var projectId1 = ProjectId.CreateNewId();
            var projectId2 = ProjectId.CreateNewId();
            var versionStamp = VersionStamp.Create();

            solution = solution.AddProject(ProjectInfo.Create(
                projectId1, versionStamp, "Project1", "Project1", LanguageNames.CSharp));
            solution = solution.AddProject(ProjectInfo.Create(
                projectId2, versionStamp, "Project2", "Project2", LanguageNames.CSharp));

            var project1 = solution.GetProject(projectId1)!;
            var document1 = project1.AddDocument("File1.cs", SourceText.From("class A { }"));
            solution = document1.Project.Solution;

            var project2 = solution.GetProject(projectId2)!;
            var document2 = project2.AddDocument("File2.cs", SourceText.From("class B { }"));
            solution = document2.Project.Solution;

            // Create AnalysisContext
            var contextType = typeof(SolutionAnalyzerService)
                .GetNestedType("AnalysisContext", BindingFlags.NonPublic)!;
            var context = contextType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
            contextType.GetProperty("MaxDiagnostics")!.SetValue(context, 100);

            // Act — call AnalyzeProjectsAsync via reflection
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "AnalyzeProjectsAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = await (Task<(List<DiagnosticDetail>, DiagnosticSummary, AnalyzerLoadReport)>)
                method.Invoke(_sut, new object?[] { solution, context, null, 0, CancellationToken.None })!;

            // Assert
            var (diagnostics, summary, _) = result;
            diagnostics.ShouldNotBeNull();
            summary.ShouldNotBeNull();
        }

        [Fact]
        public async Task Should_Report_Progress_For_Each_Analyzed_Project()
        {
            // Arrange — two projects, mirroring Should_Analyze_All_Projects_In_Solution.
            var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;
            var versionStamp = VersionStamp.Create();
            var projectId1 = ProjectId.CreateNewId();
            var projectId2 = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(
                projectId1, versionStamp, "Project1", "Project1", LanguageNames.CSharp));
            solution = solution.AddProject(ProjectInfo.Create(
                projectId2, versionStamp, "Project2", "Project2", LanguageNames.CSharp));
            solution = solution.GetProject(projectId1)!
                .AddDocument("File1.cs", SourceText.From("class A { }")).Project.Solution;
            solution = solution.GetProject(projectId2)!
                .AddDocument("File2.cs", SourceText.From("class B { }")).Project.Solution;

            var contextType = typeof(SolutionAnalyzerService)
                .GetNestedType("AnalysisContext", BindingFlags.NonPublic)!;
            var context = contextType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
            contextType.GetProperty("MaxDiagnostics")!.SetValue(context, 100);

            // Capture progress reports synchronously (unlike Progress<T>, which posts async).
            var reports = new List<ProgressNotificationValue>();
            var progress = A.Fake<IProgress<ProgressNotificationValue>>();
            A.CallTo(() => progress.Report(A<ProgressNotificationValue>._))
                .Invokes((ProgressNotificationValue v) => reports.Add(v));

            // Act
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "AnalyzeProjectsAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task<(List<DiagnosticDetail>, DiagnosticSummary, AnalyzerLoadReport)>)
                method.Invoke(_sut, new object?[] { solution, context, progress, 0, CancellationToken.None })!;

            // Assert — a report per project, culminating in the full 2/2 count.
            reports.ShouldNotBeEmpty();
            reports.ShouldAllBe(r => r.Total == 2);
            reports.Last().Progress.ShouldBe(2f);
            reports.Count(r => r.Progress > 0).ShouldBe(2);
        }

        [Fact]
        public async Task Should_Filter_Projects_By_IncludePattern()
        {
            // Arrange
            var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;
            var versionStamp = VersionStamp.Create();

            // Add a project with "Service" in name
            var projectId = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(
                projectId, versionStamp, "MyService", "MyService", LanguageNames.CSharp));

            // Create context with include pattern that doesn't match
            var contextType = typeof(SolutionAnalyzerService)
                .GetNestedType("AnalysisContext", BindingFlags.NonPublic)!;
            var context = contextType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
            contextType.GetProperty("MaxDiagnostics")!.SetValue(context, 100);
            contextType.GetProperty("IncludePattern")!.SetValue(context, "Controller"); // Won't match "MyService"

            // Act
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "AnalyzeProjectsAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = await (Task<(List<DiagnosticDetail>, DiagnosticSummary, AnalyzerLoadReport)>)
                method.Invoke(_sut, new object?[] { solution, context, null, 0, CancellationToken.None })!;

            // Assert — project excluded by include pattern
            var (diagnostics, summary, _) = result;
            diagnostics.ShouldBeEmpty();
        }
    }

    #endregion

    #region CollectDiagnosticStatistics

    public class CollectDiagnosticStatisticsTests : SolutionAnalyzerServiceRoslynTests
    {
        [Fact]
        public async Task Should_Collect_Statistics_From_Real_Diagnostics()
        {
            // Arrange
            var (_, project) = CreateInMemoryProject("TestProject",
                "class Foo { void Bar() { int x; int y; } }");
            var compilation = await project.GetCompilationAsync();
            var diagnostics = compilation!.GetDiagnostics().ToList();

            // Act
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "CollectDiagnosticStatistics",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var resultObj = method.Invoke(_sut, new object[] { diagnostics })!;

            // Assert — result is a ValueTuple
            resultObj.ShouldNotBeNull();
            var resultType = resultObj.GetType();
            var statsField = resultType.GetField("Item1");
            var fixableField = resultType.GetField("Item2");
            statsField.ShouldNotBeNull();
            fixableField.ShouldNotBeNull();
            var stats = (DiagnosticStats)statsField!.GetValue(resultObj)!;
            stats.ShouldNotBeNull();
            stats.BySeverity.ShouldNotBeNull();
            stats.ById.ShouldNotBeNull();
        }

        [Fact]
        public void Should_Return_Empty_Stats_For_No_Diagnostics()
        {
            // Arrange
            var diagnostics = new List<Diagnostic>();

            // Act
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "CollectDiagnosticStatistics",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var resultObj = method.Invoke(_sut, new object[] { diagnostics })!;

            // Assert
            var resultType = resultObj.GetType();
            var statsField = resultType.GetField("Item1");
            var stats = (DiagnosticStats)statsField!.GetValue(resultObj)!;
            stats.ById.ShouldBeEmpty();
            stats.BySeverity.ShouldBeEmpty();
            var fixableIds = (List<string>)resultType.GetField("Item2")!.GetValue(resultObj)!;
            fixableIds.ShouldBeEmpty();
        }
    }

    #endregion

    #region GetProjectDiagnosticsAsync

    public class GetProjectDiagnosticsTests : SolutionAnalyzerServiceRoslynTests
    {
        [Fact]
        public async Task Should_Get_Diagnostics_From_Real_Compilation()
        {
            // Arrange
            var (_, project) = CreateInMemoryProject("TestProject",
                "class Foo { void Bar() { int unused; } }");
            var compilation = await project.GetCompilationAsync();
            compilation.ShouldNotBeNull();

            // Act
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "GetProjectDiagnosticsAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = (await (Task<(List<Diagnostic> Diagnostics, AnalyzerLoadReport AnalyzerLoad)>)method.Invoke(_sut,
                new object?[] { project, compilation!, (List<string>?)null, (List<string>?)null, CancellationToken.None })!).Diagnostics;

            // Assert
            result.ShouldNotBeNull();
        }

        [Fact]
        public async Task Should_Filter_By_Diagnostic_Id()
        {
            // Arrange
            var (_, project) = CreateInMemoryProject("TestProject",
                "class Foo { void Bar() { int unused; } }");
            var compilation = await project.GetCompilationAsync();
            compilation.ShouldNotBeNull();
            var allDiagnostics = compilation!.GetDiagnostics();

            // Act — filter to only show specific IDs
            var ids = allDiagnostics.Select(d => d.Id).Take(1).ToList();
            var method = typeof(SolutionAnalyzerService).GetMethod(
                "GetProjectDiagnosticsAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var result = (await (Task<(List<Diagnostic> Diagnostics, AnalyzerLoadReport AnalyzerLoad)>)method.Invoke(_sut,
                new object?[] { project, compilation!, ids, (List<string>?)null, CancellationToken.None })!).Diagnostics;

            // Assert — all results should match the filter
            result.ShouldNotBeNull();
            if (ids.Count > 0)
                result.ShouldAllBe(d => ids.Contains(d.Id));
        }
    }

    #endregion
}
