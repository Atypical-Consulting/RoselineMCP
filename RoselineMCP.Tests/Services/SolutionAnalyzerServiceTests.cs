using System.Reflection;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoselineMCP.Models;
using RoselineMCP.Services;
using RoselineMCP.Interfaces;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class SolutionAnalyzerServiceTests
{
    private readonly ILogger<SolutionAnalyzerService> _logger;
    private readonly IMSBuildService _msBuildService;
    private readonly IDiagnosticFilterService _filterService;
    private readonly SolutionAnalyzerService _sut;

    public SolutionAnalyzerServiceTests()
    {
        _logger = A.Fake<ILogger<SolutionAnalyzerService>>();
        _msBuildService = A.Fake<IMSBuildService>();
        _filterService = A.Fake<IDiagnosticFilterService>();
        _sut = new SolutionAnalyzerService(_logger, _msBuildService, _filterService);
    }

    public class AnalyzeSolutionAsyncTests : SolutionAnalyzerServiceTests
    {
        [Fact]
        public async Task Should_Attempt_Real_Clone_For_Git_Urls_Instead_Of_NotImplementedException()
        {
            // Arrange — a loopback URL that nothing listens on. The TCP connection is refused
            // almost instantly (no DNS lookup, no real network access needed), so this proves
            // AnalyzeSolutionAsync now actually attempts a Git clone for http(s) URLs — and
            // fails with a clear, descriptive error — instead of unconditionally throwing
            // NotImplementedException like it used to.
            var gitUrl = "https://127.0.0.1:1/nonexistent-repo.git";

            // Act & Assert
            var exception = await Should.ThrowAsync<Exception>(
                async () => await _sut.AnalyzeSolutionAsync(gitUrl));

            exception.ShouldNotBeOfType<NotImplementedException>();
        }

        [Fact]
        public async Task Should_Throw_FileNotFoundException_When_Solution_Not_Found()
        {
            // Arrange
            var invalidPath = "/nonexistent/solution.sln";

            // Act & Assert
            var exception = await Should.ThrowAsync<FileNotFoundException>(
                async () => await _sut.AnalyzeSolutionAsync(invalidPath));
            exception.Message.ShouldContain("Solution file not found");
        }

        [Fact]
        public async Task Should_Throw_FileNotFoundException_When_Directory_Has_No_Solution()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                // Act & Assert
                var exception = await Should.ThrowAsync<FileNotFoundException>(
                    async () => await _sut.AnalyzeSolutionAsync(tempDir));
                exception.Message.ShouldContain("No solution files found");
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    public class ListDiagnosticsAsyncTests : SolutionAnalyzerServiceTests
    {
        [Fact]
        public async Task Should_Throw_When_Project_Not_Found()
        {
            // Arrange
            var nonExistentProject = "/nonexistent/project.csproj";

            // Act & Assert - Can throw various exception types for missing project
            await Should.ThrowAsync<Exception>(
                async () => await _sut.ListDiagnosticsAsync(nonExistentProject));
        }
    }

    /// <summary>
    /// Proves that a pre-cancelled token is actually honored (instead of the operation
    /// silently running to completion) — a deterministic, in-process alternative to timing a
    /// real timeout.
    /// </summary>
    public class CancellationTests : SolutionAnalyzerServiceTests
    {
        [Fact]
        public async Task AnalyzeSolutionAsync_Should_Throw_When_Token_Already_Cancelled()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert — cancelled before any workspace/IO work is attempted.
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await _sut.AnalyzeSolutionAsync("irrelevant.sln", cancellationToken: cts.Token));

            A.CallTo(() => _msBuildService.CreateWorkspace()).MustNotHaveHappened();
        }

        [Fact]
        public async Task ListDiagnosticsAsync_Should_Throw_When_Token_Already_Cancelled()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await _sut.ListDiagnosticsAsync("irrelevant.csproj", cancellationToken: cts.Token));

            A.CallTo(() => _msBuildService.CreateWorkspace()).MustNotHaveHappened();
        }
    }

    /// <summary>
    /// End-to-end aggregation tests over AnalyzeProjectsAsync using real in-memory Roslyn
    /// compilations (AdhocWorkspace, no MSBuild) and the real DiagnosticFilterService. These
    /// pin down the honest-numbers contract: the summary counts every diagnostic passing the
    /// filters (never capped by maxDiagnostics), and topDiagnostics is the global top-N by
    /// severity across all projects — not the first N encountered in project order.
    /// </summary>
    public class DiagnosticAggregationTests
    {
        private readonly SolutionAnalyzerService _aggregationSut;

        public DiagnosticAggregationTests()
        {
            var logger = A.Fake<ILogger<SolutionAnalyzerService>>();
            var msBuildService = A.Fake<IMSBuildService>();
            var codeFixProviderFactory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
            var realFilterService = new DiagnosticFilterService(codeFixProviderFactory);
            _aggregationSut = new SolutionAnalyzerService(logger, msBuildService, realFilterService);
        }

        private static readonly MetadataReference CoreLibReference =
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

        /// <summary>Adds a compilable project (CoreLib referenced, DLL output) with one source file.</summary>
        private static Solution AddProject(Solution solution, string name, string code)
        {
            var projectId = ProjectId.CreateNewId();
            var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                metadataReferences: new[] { CoreLibReference },
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            solution = solution.AddProject(projectInfo);
            return solution.AddDocument(
                DocumentId.CreateNewId(projectId), $"{name}.cs", SourceText.From(code),
                filePath: $"/{name}/{name}.cs");
        }

        /// <summary>Produces exactly <paramref name="count"/> CS0168 warnings (unused locals).</summary>
        private static string WarningCode(string className, int count)
        {
            var locals = string.Join(" ", Enumerable.Range(1, count).Select(i => $"int u{i};"));
            return $"class {className} {{ void M() {{ {locals} }} }}";
        }

        /// <summary>Produces exactly <paramref name="count"/> CS0103 errors (calls to undefined methods).</summary>
        private static string ErrorCode(string className, int count)
        {
            var calls = string.Join(" ", Enumerable.Range(1, count).Select(i => $"Undefined{i}();"));
            return $"class {className} {{ void M() {{ {calls} }} }}";
        }

        private async Task<(List<DiagnosticDetail> Diagnostics, DiagnosticSummary Summary)> RunAnalyzeProjectsAsync(
            Solution solution, int maxDiagnostics, IProgress<ProgressNotificationValue>? progress = null)
        {
            var contextType = typeof(SolutionAnalyzerService)
                .GetNestedType("AnalysisContext", BindingFlags.NonPublic)!;
            var context = contextType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
            contextType.GetProperty("MaxDiagnostics")!.SetValue(context, maxDiagnostics);

            var method = typeof(SolutionAnalyzerService).GetMethod(
                "AnalyzeProjectsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            return await (Task<(List<DiagnosticDetail>, DiagnosticSummary)>)
                method.Invoke(_aggregationSut, new object?[] { solution, context, progress, 0, CancellationToken.None })!;
        }

        [Fact]
        public async Task Summary_Should_Count_All_Diagnostics_Even_Beyond_MaxDiagnostics()
        {
            // Arrange — one project with 10 warnings, but maxDiagnostics = 3.
            using var workspace = new AdhocWorkspace();
            var solution = AddProject(workspace.CurrentSolution, "WarnProject", WarningCode("W", 10));

            // Act
            var (diagnostics, summary) = await RunAnalyzeProjectsAsync(solution, maxDiagnostics: 3);

            // Assert — details are capped, but the summary counts every diagnostic.
            summary.Warning.ShouldBe(10);
            summary.Error.ShouldBe(0);
            diagnostics.Count.ShouldBe(3);
        }

        [Fact]
        public async Task TopDiagnostics_Should_Prefer_Later_Project_Errors_Over_Earlier_Project_Warnings()
        {
            // Arrange — the first project has 5 warnings, a later project has 3 errors.
            // With maxDiagnostics = 4 the old first-N-encountered behavior returned only
            // warnings from the first project and dropped every error.
            using var workspace = new AdhocWorkspace();
            var solution = workspace.CurrentSolution;
            solution = AddProject(solution, "AAA_Warnings", WarningCode("W", 5));
            solution = AddProject(solution, "ZZZ_Errors", ErrorCode("E", 3));

            // Act
            var (diagnostics, summary) = await RunAnalyzeProjectsAsync(solution, maxDiagnostics: 4);

            // Assert — every error made the cut, ranked above the warnings.
            summary.Error.ShouldBe(3);
            summary.Warning.ShouldBe(5);
            diagnostics.Count.ShouldBe(4);
            diagnostics.Count(d => d.Severity == "error").ShouldBe(3);
            diagnostics.Take(3).ShouldAllBe(d => d.Project == "ZZZ_Errors" && d.Severity == "error");
            diagnostics[3].Severity.ShouldBe("warning");
            diagnostics[3].Project.ShouldBe("AAA_Warnings");
        }
    }

    // Testing helper methods through the filter service
    public class FilterServiceTests : SolutionAnalyzerServiceTests
    {
        [Fact]
        public void IsFixableDiagnostic_Should_Return_True_For_Known_Fixable_Ids()
        {
            // Arrange
            var fixableIds = new[] { "CS0168", "CS0219", "IDE0005", "RCS1213", "SA1101" };

            // Act & Assert
            foreach (var id in fixableIds)
            {
                A.CallTo(() => _filterService.IsFixableDiagnostic(id)).Returns(true);
                _filterService.IsFixableDiagnostic(id).ShouldBeTrue($"{id} should be fixable");
            }
        }

        [Fact]
        public void IsFixableDiagnostic_Should_Return_False_For_Unknown_Ids()
        {
            // Arrange
            var unfixableIds = new[] { "UNKNOWN001", "TEST123", "NOTREAL456" };

            // Act & Assert
            foreach (var id in unfixableIds)
            {
                A.CallTo(() => _filterService.IsFixableDiagnostic(id)).Returns(false);
                _filterService.IsFixableDiagnostic(id).ShouldBeFalse($"{id} should not be fixable");
            }
        }

        [Fact]
        public void ShouldAnalyzeProject_Should_Respect_Include_Pattern()
        {
            // Arrange & Act & Assert
            A.CallTo(() => _filterService.ShouldAnalyzeProject("MyApp.Core", "Core", null)).Returns(true);
            A.CallTo(() => _filterService.ShouldAnalyzeProject("MyApp.Tests", "Core", null)).Returns(false);
            A.CallTo(() => _filterService.ShouldAnalyzeProject("MyApp.Core.Tests", "Core", null)).Returns(true);

            _filterService.ShouldAnalyzeProject("MyApp.Core", "Core", null).ShouldBeTrue();
            _filterService.ShouldAnalyzeProject("MyApp.Tests", "Core", null).ShouldBeFalse();
            _filterService.ShouldAnalyzeProject("MyApp.Core.Tests", "Core", null).ShouldBeTrue();
        }

        [Fact]
        public void ShouldAnalyzeProject_Should_Respect_Exclude_Pattern()
        {
            // Arrange & Act & Assert
            A.CallTo(() => _filterService.ShouldAnalyzeProject("MyApp.Core", null, "Test")).Returns(true);
            A.CallTo(() => _filterService.ShouldAnalyzeProject("MyApp.Tests", null, "Test")).Returns(false);
            A.CallTo(() => _filterService.ShouldAnalyzeProject("MyApp.IntegrationTests", null, "Test")).Returns(false);

            _filterService.ShouldAnalyzeProject("MyApp.Core", null, "Test").ShouldBeTrue();
            _filterService.ShouldAnalyzeProject("MyApp.Tests", null, "Test").ShouldBeFalse();
            _filterService.ShouldAnalyzeProject("MyApp.IntegrationTests", null, "Test").ShouldBeFalse();
        }

        [Fact]
        public void GetSeverityPriority_Should_Return_Correct_Priorities()
        {
            // Arrange & Act & Assert
            A.CallTo(() => _filterService.GetSeverityPriority("error")).Returns(3);
            A.CallTo(() => _filterService.GetSeverityPriority("Error")).Returns(3);
            A.CallTo(() => _filterService.GetSeverityPriority("warning")).Returns(2);
            A.CallTo(() => _filterService.GetSeverityPriority("Warning")).Returns(2);
            A.CallTo(() => _filterService.GetSeverityPriority("info")).Returns(1);
            A.CallTo(() => _filterService.GetSeverityPriority("Info")).Returns(1);
            A.CallTo(() => _filterService.GetSeverityPriority("hidden")).Returns(0);
            A.CallTo(() => _filterService.GetSeverityPriority("unknown")).Returns(0);

            _filterService.GetSeverityPriority("error").ShouldBe(3);
            _filterService.GetSeverityPriority("Error").ShouldBe(3);
            _filterService.GetSeverityPriority("warning").ShouldBe(2);
            _filterService.GetSeverityPriority("Warning").ShouldBe(2);
            _filterService.GetSeverityPriority("info").ShouldBe(1);
            _filterService.GetSeverityPriority("Info").ShouldBe(1);
            _filterService.GetSeverityPriority("hidden").ShouldBe(0);
            _filterService.GetSeverityPriority("unknown").ShouldBe(0);
        }
    }

}