using System.Text.Json;
using FakeItEasy;
using RoselineMCP.Models;
using RoselineMCP.Services;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

public class AnalysisToolsTests
{
    public class AnalyzeSolutionTests
    {
        private readonly ISolutionAnalyzerService _analyzerService;

        public AnalyzeSolutionTests()
        {
            _analyzerService = A.Fake<ISolutionAnalyzerService>();
        }

        [Fact]
        public async Task Should_Return_Json_Response_On_Success()
        {
            // Arrange
            var expectedResponse = new AnalyzeSolutionResponse
            {
                Solution = "Test.sln",
                Projects = 3,
                DiagnosticSummary = new DiagnosticSummary { Error = 1, Warning = 5 },
                TopDiagnostics = new List<DiagnosticDetail>()
            };

            A.CallTo(() => _analyzerService.AnalyzeSolutionAsync(
                A<string>._,
                A<string?>._,
                A<string?>._,
                A<string?>._,
                A<string?>._,
                A<int>._))
                .Returns(Task.FromResult(expectedResponse));

            // Act
            var result = await AnalysisTools.AnalyzeSolution(
                _analyzerService,
                "test.sln",
                null,
                null,
                null,
                null,
                100);

            // Assert
            result.ShouldNotBeNullOrEmpty();
            var parsedResult = JsonSerializer.Deserialize<AnalyzeSolutionResponse>(result);
            parsedResult.ShouldNotBeNull();
            parsedResult.Solution.ShouldBe("Test.sln");
            parsedResult.Projects.ShouldBe(3);
        }

        [Fact]
        public async Task Should_Return_Error_Json_On_Exception()
        {
            // Arrange
            A.CallTo(() => _analyzerService.AnalyzeSolutionAsync(
                A<string>._,
                A<string?>._,
                A<string?>._,
                A<string?>._,
                A<string?>._,
                A<int>._))
                .Throws(new FileNotFoundException("Solution not found"));

            // Act
            var result = await AnalysisTools.AnalyzeSolution(
                _analyzerService,
                "test.sln",
                null,
                null,
                null,
                null,
                100);

            // Assert
            result.ShouldContain("error");
            result.ShouldContain("Solution not found");
            result.ShouldContain("FileNotFoundException");
        }

        [Fact]
        public async Task Should_Pass_All_Parameters_To_Service()
        {
            // Arrange
            var pathOrGit = "test.sln";
            var branch = "main";
            var include = "Core";
            var exclude = "Test";
            var severity = "Warning";
            var maxDiagnostics = 50;

            A.CallTo(() => _analyzerService.AnalyzeSolutionAsync(
                pathOrGit,
                branch,
                include,
                exclude,
                severity,
                maxDiagnostics))
                .Returns(Task.FromResult(new AnalyzeSolutionResponse()));

            // Act
            await AnalysisTools.AnalyzeSolution(
                _analyzerService,
                pathOrGit,
                branch,
                include,
                exclude,
                severity,
                maxDiagnostics);

            // Assert
            A.CallTo(() => _analyzerService.AnalyzeSolutionAsync(
                pathOrGit,
                branch,
                include,
                exclude,
                severity,
                maxDiagnostics))
                .MustHaveHappenedOnceExactly();
        }
    }

    public class ListDiagnosticsTests
    {
        private readonly ISolutionAnalyzerService _analyzerService;

        public ListDiagnosticsTests()
        {
            _analyzerService = A.Fake<ISolutionAnalyzerService>();
        }

        [Fact]
        public async Task Should_Return_Json_Response_On_Success()
        {
            // Arrange
            var expectedResponse = new ListDiagnosticsResponse
            {
                Project = "TestProject",
                TotalDiagnostics = 10,
                Diagnostics = new List<DiagnosticDetail>(),
                Stats = new DiagnosticStats(),
                SuggestedFixableIds = new List<string> { "CS0168", "IDE0005" }
            };

            A.CallTo(() => _analyzerService.ListDiagnosticsAsync(
                A<string>._,
                A<List<string>?>._,
                A<List<string>?>._,
                A<int>._))
                .Returns(Task.FromResult(expectedResponse));

            // Act
            var result = await AnalysisTools.ListDiagnostics(
                _analyzerService,
                "TestProject",
                null,
                null,
                100);

            // Assert
            result.ShouldNotBeNullOrEmpty();
            var parsedResult = JsonSerializer.Deserialize<ListDiagnosticsResponse>(result);
            parsedResult.ShouldNotBeNull();
            parsedResult.Project.ShouldBe("TestProject");
            parsedResult.TotalDiagnostics.ShouldBe(10);
        }

        [Fact]
        public async Task Should_Convert_Arrays_To_Lists()
        {
            // Arrange
            var ids = new[] { "CS0168", "CS0219" };
            var files = new[] { "Controller.cs", "Service.cs" };
            List<string>? capturedIds = null;
            List<string>? capturedFiles = null;

            A.CallTo(() => _analyzerService.ListDiagnosticsAsync(
                A<string>._,
                A<List<string>?>._,
                A<List<string>?>._,
                A<int>._))
                .Invokes((string p, List<string>? i, List<string>? f, int m) =>
                {
                    capturedIds = i;
                    capturedFiles = f;
                })
                .Returns(Task.FromResult(new ListDiagnosticsResponse()));

            // Act
            await AnalysisTools.ListDiagnostics(
                _analyzerService,
                "TestProject",
                ids,
                files,
                100);

            // Assert
            capturedIds.ShouldNotBeNull();
            capturedIds.Count.ShouldBe(2);
            capturedIds.ShouldContain("CS0168");
            capturedFiles.ShouldNotBeNull();
            capturedFiles.Count.ShouldBe(2);
            capturedFiles.ShouldContain("Controller.cs");
        }

        [Fact]
        public async Task Should_Handle_Null_Arrays()
        {
            // Arrange
            A.CallTo(() => _analyzerService.ListDiagnosticsAsync(
                A<string>._,
                null,
                null,
                A<int>._))
                .Returns(Task.FromResult(new ListDiagnosticsResponse()));

            // Act
            var result = await AnalysisTools.ListDiagnostics(
                _analyzerService,
                "TestProject",
                null,
                null,
                100);

            // Assert
            result.ShouldNotBeNullOrEmpty();
        }
    }

    public class ApplyFixesTests
    {
        private readonly ICodeFixService _codeFixService;

        public ApplyFixesTests()
        {
            _codeFixService = A.Fake<ICodeFixService>();
        }

        [Fact]
        public async Task Should_Return_Error_When_No_Ids_Provided()
        {
            // Act
            var result = await AnalysisTools.ApplyFixes(
                _codeFixService,
                "TestProject",
                Array.Empty<string>(),
                false);

            // Assert
            result.ShouldContain("\"error\"");
            result.ShouldContain("No diagnostic IDs provided");
            result.ShouldContain("ValidationError");
        }

        [Fact]
        public async Task Should_Return_Json_Response_On_Success()
        {
            // Arrange
            var expectedResponse = new ApplyFixesResponse
            {
                Project = "TestProject",
                FixersApplied = new List<string> { "CS0168" },
                ChangedFiles = new List<string> { "Program.cs" },
                FixedCount = 1,
                PreviewOnly = true
            };

            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                A<bool>._))
                .Returns(Task.FromResult(expectedResponse));

            // Act
            var result = await AnalysisTools.ApplyFixes(
                _codeFixService,
                "TestProject",
                new[] { "CS0168" },
                true);

            // Assert
            result.ShouldNotBeNullOrEmpty();
            var parsedResult = JsonSerializer.Deserialize<ApplyFixesResponse>(result);
            parsedResult.ShouldNotBeNull();
            parsedResult.Project.ShouldBe("TestProject");
            parsedResult.FixedCount.ShouldBe(1);
            parsedResult.PreviewOnly.ShouldBeTrue();
        }

        [Fact]
        public async Task Should_Pass_Parameters_Correctly()
        {
            // Arrange
            var project = "TestProject";
            var ids = new[] { "CS0168", "IDE0005" };
            var previewOnly = false;
            List<string>? capturedIds = null;

            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                project,
                A<List<string>>._,
                previewOnly))
                .Invokes((string p, List<string> i, bool pr) =>
                {
                    capturedIds = i;
                })
                .Returns(Task.FromResult(new ApplyFixesResponse()));

            // Act
            await AnalysisTools.ApplyFixes(
                _codeFixService,
                project,
                ids,
                previewOnly);

            // Assert
            capturedIds.ShouldNotBeNull();
            capturedIds.Count.ShouldBe(2);
            capturedIds.ShouldContain("CS0168");
            capturedIds.ShouldContain("IDE0005");
        }
    }

    public class CreatePatchTests
    {
        private readonly IPatchService _patchService;

        public CreatePatchTests()
        {
            _patchService = A.Fake<IPatchService>();
        }

        [Fact]
        public void Should_Return_Json_Response_On_Success()
        {
            // Arrange
            var expectedResponse = new CreatePatchResponse
            {
                Patch = "--- a/file.txt\n+++ b/file.txt",
                HasChanges = true,
                LinesAdded = 1,
                LinesRemoved = 0,
                FileName = "test.txt",
                Summary = "test.txt: +1 lines"
            };

            A.CallTo(() => _patchService.CreatePatch(
                A<string>._,
                A<string>._,
                A<string?>._))
                .Returns(expectedResponse);

            // Act
            var result = AnalysisTools.CreatePatch(
                _patchService,
                "old content",
                "new content",
                "test.txt");

            // Assert
            result.ShouldNotBeNullOrEmpty();
            var parsedResult = JsonSerializer.Deserialize<CreatePatchResponse>(result);
            parsedResult.ShouldNotBeNull();
            parsedResult.HasChanges.ShouldBeTrue();
            parsedResult.FileName.ShouldBe("test.txt");
        }

        [Fact]
        public void Should_Return_Error_Json_On_Exception()
        {
            // Arrange
            A.CallTo(() => _patchService.CreatePatch(
                A<string>._,
                A<string>._,
                A<string?>._))
                .Throws(new InvalidOperationException("Failed to create patch"));

            // Act
            var result = AnalysisTools.CreatePatch(
                _patchService,
                "old",
                "new",
                null);

            // Assert
            result.ShouldContain("error");
            result.ShouldContain("Failed to create patch");
            result.ShouldContain("InvalidOperationException");
        }

        [Fact]
        public void Should_Pass_Null_FileName_When_Not_Provided()
        {
            // Arrange
            string? capturedFileName = "not-null";
            A.CallTo(() => _patchService.CreatePatch(
                A<string>._,
                A<string>._,
                A<string?>._))
                .Invokes((string b, string a, string? f) =>
                {
                    capturedFileName = f;
                })
                .Returns(new CreatePatchResponse());

            // Act
            AnalysisTools.CreatePatch(
                _patchService,
                "old",
                "new",
                null);

            // Assert
            capturedFileName.ShouldBeNull();
        }
    }
}