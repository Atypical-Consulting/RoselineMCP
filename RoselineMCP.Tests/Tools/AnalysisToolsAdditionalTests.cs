using FakeItEasy;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

/// <summary>
/// Additional tests to improve coverage on ApplyFixesTool and ListDiagnosticsTool.
/// </summary>
public class AnalysisToolsAdditionalTests
{
    public class ApplyFixesAdditionalTests
    {
        private readonly ICodeFixService _codeFixService;

        public ApplyFixesAdditionalTests()
        {
            _codeFixService = A.Fake<ICodeFixService>();
        }

        [Fact]
        public async Task Should_Return_Error_Envelope_On_Exception()
        {
            // Arrange
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                A<bool>._,
                A<bool>._,
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Throws(new InvalidOperationException("Workspace failed to load"));

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                new[] { "CS0168" },
                "TestProject");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Message.ShouldContain("Workspace failed to load");
            result.Error.Type.ShouldBe("AnalysisError");
        }

        [Fact]
        public async Task Should_Return_Error_For_Null_Ids()
        {
            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                null!,
                "TestProject");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Message.ShouldContain("No diagnostic IDs provided");
        }

        [Fact]
        public async Task Should_Apply_Fix_With_PreviewOnly_False()
        {
            // Arrange
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                false,
                A<bool>._,
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Returns(Task.FromResult(new ApplyFixesResponse
                {
                    PreviewOnly = false,
                    FixedCount = 3
                }));

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                new[] { "CS0168" },
                "TestProject",
                false);

            // Assert
            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.PreviewOnly.ShouldBeFalse();
            result.Data.FixedCount.ShouldBe(3);
        }

        [Fact]
        public async Task Should_Return_NotFound_Error_When_File_Not_Found()
        {
            // Arrange
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                A<bool>._,
                A<bool>._,
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Throws(new FileNotFoundException("Project file not found"));

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                new[] { "CS0168" },
                "missing.csproj");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("NotFoundError");
        }

        /// <summary>
        /// Proves that when the underlying service reports cancellation, the tool never lets the
        /// exception escape to the MCP layer — it renders a graceful failure envelope instead, per
        /// the project's "never throw to MCP" convention.
        /// </summary>
        [Fact]
        public async Task Should_Return_Graceful_Envelope_When_Service_Reports_Cancellation()
        {
            // Arrange
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                A<bool>._,
                A<bool>._,
                A<int>._,
                A<IProgress<ProgressNotificationValue>?>._,
                A<CancellationToken>._))
                .Throws(new OperationCanceledException());

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                new[] { "CS0168" },
                "TestProject");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("CancelledError");
        }
    }

    public class ListDiagnosticsAdditionalTests
    {
        private readonly ISolutionAnalyzerService _analyzerService;

        public ListDiagnosticsAdditionalTests()
        {
            _analyzerService = A.Fake<ISolutionAnalyzerService>();
        }

        [Fact]
        public async Task Should_Return_Error_Envelope_On_Exception()
        {
            // Arrange
            A.CallTo(() => _analyzerService.ListDiagnosticsAsync(
                A<string>._,
                A<List<string>?>._,
                A<List<string>?>._,
                A<int>._,
                A<CancellationToken>._))
                .Throws(new InvalidOperationException("Project not found in solution"));

            // Act
            var result = await ListDiagnosticsTool.ListDiagnostics(
                _analyzerService,
                "TestProject");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Message.ShouldContain("Project not found in solution");
            result.Error.Type.ShouldBe("AnalysisError");
        }

        [Fact]
        public async Task Should_Return_Error_Envelope_On_File_Not_Found()
        {
            // Arrange
            A.CallTo(() => _analyzerService.ListDiagnosticsAsync(
                A<string>._,
                A<List<string>?>._,
                A<List<string>?>._,
                A<int>._,
                A<CancellationToken>._))
                .Throws(new FileNotFoundException("Solution file not found"));

            // Act
            var result = await ListDiagnosticsTool.ListDiagnostics(
                _analyzerService,
                "/nonexistent/project.csproj");

            // Assert
            result.Ok.ShouldBeFalse();
            result.Error.ShouldNotBeNull();
            result.Error.Type.ShouldBe("NotFoundError");
        }

        [Fact]
        public async Task Should_Pass_Custom_Max_To_Service()
        {
            // Arrange
            int capturedMax = 0;
            A.CallTo(() => _analyzerService.ListDiagnosticsAsync(
                A<string>._,
                A<List<string>?>._,
                A<List<string>?>._,
                A<int>._,
                A<CancellationToken>._))
                .Invokes((string _, List<string>? _, List<string>? _, int max, CancellationToken _) =>
                {
                    capturedMax = max;
                })
                .Returns(Task.FromResult(new ListDiagnosticsResponse()));

            // Act
            await ListDiagnosticsTool.ListDiagnostics(
                _analyzerService,
                "TestProject",
                null,
                null,
                200);

            // Assert
            capturedMax.ShouldBe(200);
        }

        [Fact]
        public async Task Should_Return_Success_Envelope_With_Data()
        {
            // Arrange
            A.CallTo(() => _analyzerService.ListDiagnosticsAsync(
                A<string>._,
                A<List<string>?>._,
                A<List<string>?>._,
                A<int>._,
                A<CancellationToken>._))
                .Returns(Task.FromResult(new ListDiagnosticsResponse
                {
                    Project = "MyProject",
                    TotalDiagnostics = 5
                }));

            // Act
            var result = await ListDiagnosticsTool.ListDiagnostics(
                _analyzerService,
                "MyProject");

            // Assert
            result.Ok.ShouldBeTrue();
            result.Data.ShouldNotBeNull();
            result.Data.Project.ShouldBe("MyProject");
            result.Data.TotalDiagnostics.ShouldBe(5);
        }
    }
}
