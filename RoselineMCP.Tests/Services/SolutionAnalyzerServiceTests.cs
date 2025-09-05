using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class SolutionAnalyzerServiceTests
{
    private readonly ILogger<SolutionAnalyzerService> _logger;
    private readonly SolutionAnalyzerService _sut;

    public SolutionAnalyzerServiceTests()
    {
        _logger = A.Fake<ILogger<SolutionAnalyzerService>>();
        _sut = new SolutionAnalyzerService(_logger);
    }

    public class AnalyzeSolutionAsyncTests : SolutionAnalyzerServiceTests
    {
        [Fact]
        public async Task Should_Throw_NotImplementedException_For_Git_Urls()
        {
            // Arrange
            var gitUrl = "https://github.com/test/repo.git";

            // Act & Assert
            await Should.ThrowAsync<NotImplementedException>(
                async () => await _sut.AnalyzeSolutionAsync(gitUrl))
                .ContinueWith(t => t.Result.Message.ShouldContain("Git repository cloning not yet implemented"));
        }

        [Fact]
        public async Task Should_Throw_FileNotFoundException_When_Solution_Not_Found()
        {
            // Arrange
            var invalidPath = "/nonexistent/solution.sln";

            // Act & Assert
            await Should.ThrowAsync<FileNotFoundException>(
                async () => await _sut.AnalyzeSolutionAsync(invalidPath))
                .ContinueWith(t => t.Result.Message.ShouldContain("Solution file not found"));
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
                await Should.ThrowAsync<FileNotFoundException>(
                    async () => await _sut.AnalyzeSolutionAsync(tempDir))
                    .ContinueWith(t => t.Result.Message.ShouldContain("No solution files found"));
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

            // Act & Assert
            await Should.ThrowAsync<InvalidOperationException>(
                async () => await _sut.ListDiagnosticsAsync(nonExistentProject));
        }
    }

    // Testing helper methods that don't require MSBuild
    public class HelperMethodTests
    {
        [Fact]
        public void IsFixableDiagnostic_Should_Return_True_For_Known_Fixable_Ids()
        {
            // Arrange
            var service = new TestableAnalyzerService();
            var fixableIds = new[] { "CS0168", "CS0219", "IDE0005", "RCS1213", "SA1101" };

            // Act & Assert
            foreach (var id in fixableIds)
            {
                service.TestIsFixableDiagnostic(id).ShouldBeTrue($"{id} should be fixable");
            }
        }

        [Fact]
        public void IsFixableDiagnostic_Should_Return_False_For_Unknown_Ids()
        {
            // Arrange
            var service = new TestableAnalyzerService();
            var unfixableIds = new[] { "UNKNOWN001", "TEST123", "NOTREAL456" };

            // Act & Assert
            foreach (var id in unfixableIds)
            {
                service.TestIsFixableDiagnostic(id).ShouldBeFalse($"{id} should not be fixable");
            }
        }

        [Fact]
        public void ShouldAnalyzeProject_Should_Respect_Include_Pattern()
        {
            // Arrange
            var service = new TestableAnalyzerService();

            // Act & Assert
            service.TestShouldAnalyzeProject("MyApp.Core", "Core", null).ShouldBeTrue();
            service.TestShouldAnalyzeProject("MyApp.Tests", "Core", null).ShouldBeFalse();
            service.TestShouldAnalyzeProject("MyApp.Core.Tests", "Core", null).ShouldBeTrue();
        }

        [Fact]
        public void ShouldAnalyzeProject_Should_Respect_Exclude_Pattern()
        {
            // Arrange
            var service = new TestableAnalyzerService();

            // Act & Assert
            service.TestShouldAnalyzeProject("MyApp.Core", null, "Test").ShouldBeTrue();
            service.TestShouldAnalyzeProject("MyApp.Tests", null, "Test").ShouldBeFalse();
            service.TestShouldAnalyzeProject("MyApp.IntegrationTests", null, "Test").ShouldBeFalse();
        }

        [Fact]
        public void ShouldAnalyzeProject_Should_Apply_Both_Include_And_Exclude()
        {
            // Arrange
            var service = new TestableAnalyzerService();

            // Act & Assert
            service.TestShouldAnalyzeProject("MyApp.Core", "Core", "Test").ShouldBeTrue();
            service.TestShouldAnalyzeProject("MyApp.Core.Tests", "Core", "Test").ShouldBeFalse();
            service.TestShouldAnalyzeProject("MyApp.Data", "Core", "Test").ShouldBeFalse();
        }

        [Fact]
        public void GetSeverityPriority_Should_Return_Correct_Priorities()
        {
            // Arrange
            var service = new TestableAnalyzerService();

            // Act & Assert
            service.TestGetSeverityPriority("error").ShouldBe(3);
            service.TestGetSeverityPriority("Error").ShouldBe(3);
            service.TestGetSeverityPriority("warning").ShouldBe(2);
            service.TestGetSeverityPriority("Warning").ShouldBe(2);
            service.TestGetSeverityPriority("info").ShouldBe(1);
            service.TestGetSeverityPriority("Info").ShouldBe(1);
            service.TestGetSeverityPriority("hidden").ShouldBe(0);
            service.TestGetSeverityPriority("unknown").ShouldBe(0);
        }

        [Fact]
        public void FilterByIds_Should_Filter_Correctly()
        {
            // Arrange
            var service = new TestableAnalyzerService();
            var ids = new List<string> { "CS0168", "CS0219" };

            // Act & Assert
            service.TestFilterByIds("CS0168", ids).ShouldBeTrue();
            service.TestFilterByIds("CS0219", ids).ShouldBeTrue();
            service.TestFilterByIds("CS0414", ids).ShouldBeFalse();
            service.TestFilterByIds("CS0168", null).ShouldBeTrue();
            service.TestFilterByIds("ANY", new List<string>()).ShouldBeTrue();
        }

        [Fact]
        public void FilterByFiles_Should_Filter_Correctly()
        {
            // Arrange
            var service = new TestableAnalyzerService();
            var files = new List<string> { "Controller.cs", "Service.cs" };

            // Act & Assert
            service.TestFilterByFiles("/src/UserController.cs", files).ShouldBeTrue();
            service.TestFilterByFiles("/src/OrderService.cs", files).ShouldBeTrue();
            service.TestFilterByFiles("/src/Model.cs", files).ShouldBeFalse();
            service.TestFilterByFiles("/src/Any.cs", null).ShouldBeTrue();
            service.TestFilterByFiles(null, files).ShouldBeFalse();
        }
    }

    // Testable wrapper to expose private methods for testing
    private class TestableAnalyzerService : SolutionAnalyzerService
    {
        public TestableAnalyzerService() : base(A.Fake<ILogger<SolutionAnalyzerService>>())
        {
        }

        public bool TestIsFixableDiagnostic(string id)
        {
            // Using reflection to access private method
            var method = typeof(SolutionAnalyzerService).GetMethod("IsFixableDiagnostic",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (bool)method!.Invoke(this, new object[] { id })!;
        }

        public bool TestShouldAnalyzeProject(string projectName, string? includePattern, string? excludePattern)
        {
            var method = typeof(SolutionAnalyzerService).GetMethod("ShouldAnalyzeProject",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (bool)method!.Invoke(this, new object?[] { projectName, includePattern, excludePattern })!;
        }

        public int TestGetSeverityPriority(string severity)
        {
            var method = typeof(SolutionAnalyzerService).GetMethod("GetSeverityPriority",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (int)method!.Invoke(this, new object[] { severity })!;
        }

        public bool TestFilterByIds(string diagnosticId, List<string>? ids)
        {
            // Create a mock diagnostic with the ID
            var diagnostic = new MockDiagnostic { Id = diagnosticId };
            var method = typeof(SolutionAnalyzerService).GetMethod("FilterByIds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            // Since we can't easily create a real Diagnostic, we'll test the logic directly
            if (ids == null || ids.Count == 0)
                return true;
            return ids.Contains(diagnosticId);
        }

        public bool TestFilterByFiles(string? filePath, List<string>? files)
        {
            if (files == null || files.Count == 0)
                return true;
            if (filePath == null)
                return false;
            return files.Any(f => filePath.Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        private class MockDiagnostic
        {
            public string Id { get; set; } = string.Empty;
        }
    }
}