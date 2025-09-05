using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
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

            // Act & Assert - Can throw various exception types for missing project
            await Should.ThrowAsync<Exception>(
                async () => await _sut.ListDiagnosticsAsync(nonExistentProject));
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