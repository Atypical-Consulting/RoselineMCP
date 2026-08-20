using FakeItEasy;
using ModelContextProtocol;
using RoselineMCP.Models;
using RoselineMCP.Interfaces;
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
        public async Task Should_Return_Success_Envelope()
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
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Returns(Task.FromResult(expectedResponse));

            // Act
            var result = await AnalyzeSolutionTool.AnalyzeSolution(
                _analyzerService,
                "test.sln");

            // Assert
            result.Ok.ShouldBeTrue();
            result.Error.ShouldBeNull();
            result.Data.ShouldNotBeNull();
            result.Data.Solution.ShouldBe("Test.sln");
            result.Data.Projects.ShouldBe(3);
        }

        [Fact]
        public async Task Should_Return_Error_Envelope_On_Exception()
        {
            // Arrange
            A.CallTo(() => _analyzerService.AnalyzeSolutionAsync(
                A<string>._,
                A<string?>._,
                A<string?>._,
                A<string?>._,
                A<string?>._,
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Throws(new FileNotFoundException("Solution not found"));

            // Act
            var result = await AnalyzeSolutionTool.AnalyzeSolution(
                _analyzerService,
                "test.sln");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Data.ShouldBeNull();
            result.Error.ShouldNotBeNull();
            result.Error.Message.ShouldContain("Solution not found");
            result.Error.Type.ShouldBe("NotFoundError");
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
                maxDiagnostics,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Returns(Task.FromResult(new AnalyzeSolutionResponse()));

            // Act
            await AnalyzeSolutionTool.AnalyzeSolution(
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
                maxDiagnostics,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
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
        public async Task Should_Return_Success_Envelope()
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
                A<int>._,
                A<CancellationToken>._))
                .Returns(Task.FromResult(expectedResponse));

            // Act
            var result = await ListDiagnosticsTool.ListDiagnostics(
                _analyzerService,
                "TestProject");

            // Assert
            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.Project.ShouldBe("TestProject");
            result.Data.TotalDiagnostics.ShouldBe(10);
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
                A<int>._,
                A<CancellationToken>._))
                .Invokes((string _, List<string>? i, List<string>? f, int _, CancellationToken _) =>
                {
                    capturedIds = i;
                    capturedFiles = f;
                })
                .Returns(Task.FromResult(new ListDiagnosticsResponse()));

            // Act
            await ListDiagnosticsTool.ListDiagnostics(
                _analyzerService,
                "TestProject",
                ids,
                files);

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
                A<int>._,
                A<CancellationToken>._))
                .Returns(Task.FromResult(new ListDiagnosticsResponse()));

            // Act
            var result = await ListDiagnosticsTool.ListDiagnostics(
                _analyzerService,
                "TestProject");

            // Assert
            result.Ok.ShouldBeTrue();
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
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                Array.Empty<string>(),
                "TestProject");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Message.ShouldContain("No diagnostic IDs provided");
            result.Error.Type.ShouldBe("ValidationError");
        }

        /// <summary>
        /// Proves the "Read-Only by Default" safety guarantee (README.md / CLAUDE.md): calling
        /// ApplyFixes without specifying previewOnly must reach the underlying service with
        /// previewOnly=true, never previewOnly=false, so the filesystem is never touched unless
        /// the caller explicitly opts in. CodeFixServiceIntegrationTests separately proves that
        /// previewOnly=true never writes to disk end-to-end.
        /// </summary>
        [Fact]
        public async Task Should_Default_To_PreviewOnly_True_When_Not_Specified()
        {
            // Arrange
            bool? capturedPreviewOnly = null;
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                A<bool>._,
                A<bool>._,
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Invokes((string _, List<string> _, bool previewOnly, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                {
                    capturedPreviewOnly = previewOnly;
                })
                .Returns(Task.FromResult(new ApplyFixesResponse()));

            // Act - previewOnly intentionally omitted
            await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                new[] { "CS0168" },
                "TestProject");

            // Assert
            capturedPreviewOnly.ShouldNotBeNull();
            capturedPreviewOnly!.Value.ShouldBeTrue();
        }

        [Fact]
        public async Task Should_Return_Success_Envelope()
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
                A<bool>._,
                A<bool>._,
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Returns(Task.FromResult(expectedResponse));

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                new[] { "CS0168" },
                "TestProject",
                true);

            // Assert
            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.Project.ShouldBe("TestProject");
            result.Data.FixedCount.ShouldBe(1);
            result.Data.PreviewOnly.ShouldBeTrue();
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
                previewOnly,
                A<bool>._,
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Invokes((string _, List<string> i, bool _, bool _, int _, IProgress<ProgressNotificationValue>? _, CancellationToken _) =>
                {
                    capturedIds = i;
                })
                .Returns(Task.FromResult(new ApplyFixesResponse()));

            // Act
            await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                ids,
                project,
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
        public void Should_Return_Success_Envelope()
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

            A.CallTo(() => _patchService.CreatePatchWithOptions(
                A<string>._,
                A<string>._,
                A<string?>._,
                A<int>._,
                A<bool>._,
                A<bool>._,
                A<CancellationToken>._))
                .Returns(expectedResponse);

            // Act
            var result = CreatePatchTool.CreatePatch(
                _patchService,
                "old content",
                "new content",
                "test.txt");

            // Assert
            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.HasChanges.ShouldBeTrue();
            result.Data.FileName.ShouldBe("test.txt");
        }

        [Fact]
        public void Should_Return_Error_Envelope_On_Exception()
        {
            // Arrange
            A.CallTo(() => _patchService.CreatePatchWithOptions(
                A<string>._,
                A<string>._,
                A<string?>._,
                A<int>._,
                A<bool>._,
                A<bool>._,
                A<CancellationToken>._))
                .Throws(new InvalidOperationException("Failed to create patch"));

            // Act
            var result = CreatePatchTool.CreatePatch(
                _patchService,
                "old",
                "new");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Message.ShouldContain("Failed to create patch");
            result.Error.Type.ShouldBe("AnalysisError");
        }

        [Fact]
        public void Should_Pass_Null_FileName_When_Not_Provided()
        {
            // Arrange
            string? capturedFileName = "not-null";
            A.CallTo(() => _patchService.CreatePatchWithOptions(
                A<string>._,
                A<string>._,
                A<string?>._,
                A<int>._,
                A<bool>._,
                A<bool>._,
                A<CancellationToken>._))
                .Invokes((string _, string _, string? f, int _, bool _, bool _, CancellationToken _) =>
                {
                    capturedFileName = f;
                })
                .Returns(new CreatePatchResponse());

            // Act
            CreatePatchTool.CreatePatch(
                _patchService,
                "old",
                "new");

            // Assert
            capturedFileName.ShouldBeNull();
        }

        [Fact]
        public void Should_Pass_IgnoreWhitespace_And_IgnoreCase_Through_To_Service()
        {
            // Arrange
            bool? capturedIgnoreWhitespace = null;
            bool? capturedIgnoreCase = null;
            A.CallTo(() => _patchService.CreatePatchWithOptions(
                A<string>._,
                A<string>._,
                A<string?>._,
                A<int>._,
                A<bool>._,
                A<bool>._,
                A<CancellationToken>._))
                .Invokes((string _, string _, string? _, int _, bool iw, bool ic, CancellationToken _) =>
                {
                    capturedIgnoreWhitespace = iw;
                    capturedIgnoreCase = ic;
                })
                .Returns(new CreatePatchResponse());

            // Act
            CreatePatchTool.CreatePatch(
                _patchService,
                "old",
                "new",
                ignoreWhitespace: true,
                ignoreCase: true);

            // Assert
            capturedIgnoreWhitespace.ShouldBe(true);
            capturedIgnoreCase.ShouldBe(true);
        }
    }
}
