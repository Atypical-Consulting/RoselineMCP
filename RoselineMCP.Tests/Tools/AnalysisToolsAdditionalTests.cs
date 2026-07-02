using System.Text.Json;
using FakeItEasy;
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
        public async Task Should_Return_Error_Json_On_Exception()
        {
            // Arrange
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                A<bool>._,
                A<CancellationToken>._))
                .Throws(new InvalidOperationException("Workspace failed to load"));

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                "TestProject",
                new[] { "CS0168" });

            // Assert
            result.ShouldContain("\"error\"");
            result.ShouldContain("Workspace failed to load");
            result.ShouldContain("AnalysisError");
        }

        [Fact]
        public async Task Should_Return_Error_For_Null_Ids()
        {
            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                "TestProject",
                null!);

            // Assert
            result.ShouldContain("\"error\"");
            result.ShouldContain("No diagnostic IDs provided");
        }

        [Fact]
        public async Task Should_Apply_Fix_With_PreviewOnly_False()
        {
            // Arrange
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                false,
                A<CancellationToken>._))
                .Returns(Task.FromResult(new ApplyFixesResponse
                {
                    PreviewOnly = false,
                    FixedCount = 3
                }));

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                "TestProject",
                new[] { "CS0168" },
                false);

            // Assert
            var parsed = JsonSerializer.Deserialize<ApplyFixesResponse>(result);
            parsed.ShouldNotBeNull();
            parsed!.PreviewOnly.ShouldBeFalse();
            parsed.FixedCount.ShouldBe(3);
        }

        [Fact]
        public async Task Should_Return_Valid_Json_With_File_Not_Found()
        {
            // Arrange
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                A<bool>._,
                A<CancellationToken>._))
                .Throws(new FileNotFoundException("Project file not found"));

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                "missing.csproj",
                new[] { "CS0168" });

            // Assert
            result.ShouldNotBeNullOrEmpty();
            // Should be valid JSON
            var doc = JsonDocument.Parse(result);
            doc.RootElement.TryGetProperty("error", out _).ShouldBeTrue();
            doc.RootElement.TryGetProperty("type", out var typeEl).ShouldBeTrue();
            typeEl.GetString().ShouldBe("NotFoundError");
        }

        /// <summary>
        /// Proves that when the underlying service reports cancellation, the tool never lets the
        /// exception escape to the MCP layer — it renders a graceful JSON response instead, per
        /// the project's "never throw to MCP" convention.
        /// </summary>
        [Fact]
        public async Task Should_Return_Graceful_Json_When_Service_Reports_Cancellation()
        {
            // Arrange
            A.CallTo(() => _codeFixService.ApplyFixesAsync(
                A<string>._,
                A<List<string>>._,
                A<bool>._,
                A<CancellationToken>._))
                .Throws(new OperationCanceledException());

            // Act
            var result = await ApplyFixesTool.ApplyFixes(
                _codeFixService,
                "TestProject",
                new[] { "CS0168" });

            // Assert
            result.ShouldNotBeNullOrEmpty();
            var doc = JsonDocument.Parse(result);
            doc.RootElement.TryGetProperty("error", out _).ShouldBeTrue();
            doc.RootElement.TryGetProperty("type", out var typeEl).ShouldBeTrue();
            typeEl.GetString().ShouldBe("CancelledError");
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
        public async Task Should_Return_Error_Json_On_Exception()
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
            result.ShouldContain("\"error\"");
            result.ShouldContain("Project not found in solution");
            result.ShouldContain("AnalysisError");
        }

        [Fact]
        public async Task Should_Return_Error_Json_On_File_Not_Found()
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
            result.ShouldNotBeNullOrEmpty();
            var doc = JsonDocument.Parse(result);
            doc.RootElement.TryGetProperty("error", out _).ShouldBeTrue();
            doc.RootElement.TryGetProperty("type", out var typeEl).ShouldBeTrue();
            typeEl.GetString().ShouldBe("NotFoundError");
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
        public async Task Should_Return_Valid_Json_With_IndentedOutput()
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

            // Assert - should be indented JSON
            result.ShouldContain("\n");
            result.ShouldContain("MyProject");
        }
    }
}
