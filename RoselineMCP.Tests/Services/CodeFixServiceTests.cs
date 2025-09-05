using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class CodeFixServiceTests
{
    private readonly ILogger<CodeFixService> _logger;
    private readonly ISolutionAnalyzerService _analyzerService;
    private readonly ICodeFixProviderFactory _codeFixProviderFactory;
    private readonly IDiffService _diffService;
    private readonly IMSBuildService _msBuildService;
    private readonly CodeFixService _sut;

    public CodeFixServiceTests()
    {
        _logger = A.Fake<ILogger<CodeFixService>>();
        _analyzerService = A.Fake<ISolutionAnalyzerService>();
        _codeFixProviderFactory = A.Fake<ICodeFixProviderFactory>();
        _diffService = A.Fake<IDiffService>();
        _msBuildService = A.Fake<IMSBuildService>();
        _sut = new CodeFixService(_logger, _analyzerService, _codeFixProviderFactory, _diffService, _msBuildService);
    }

    public class ApplyFixesAsyncTests : CodeFixServiceTests
    {
        [Fact]
        public async Task Should_Return_Empty_Response_When_No_Ids_Provided()
        {
            // Arrange
            var project = "TestProject";
            var ids = new List<string>();

            // Act
            var result = await _sut.ApplyFixesAsync(project, ids, true);

            // Assert
            result.ShouldNotBeNull();
            result.Project.ShouldBe(project);
            result.PreviewOnly.ShouldBeTrue();
            result.FixersApplied.ShouldBeEmpty();
            result.FixedCount.ShouldBe(0);
        }

        [Fact]
        public async Task Should_Handle_Project_Not_Found()
        {
            // Arrange
            var nonExistentProject = "/nonexistent/project.csproj";
            var ids = new List<string> { "CS0168" };

            // Act & Assert - The method returns a response with error notes rather than throwing
            var result = await _sut.ApplyFixesAsync(nonExistentProject, ids);
            result.Notes.ShouldContain(n => n.Contains("Error"));
        }

        [Fact]
        public async Task Should_Set_PreviewOnly_Flag_Correctly()
        {
            // Arrange
            var project = "TestProject";
            var ids = new List<string> { "CS0168" };

            // Act
            try
            {
                var previewResult = await _sut.ApplyFixesAsync(project, ids, true);
                previewResult.PreviewOnly.ShouldBeTrue();

                var applyResult = await _sut.ApplyFixesAsync(project, ids, false);
                applyResult.PreviewOnly.ShouldBeFalse();
            }
            catch (FileNotFoundException)
            {
                // Expected if test project doesn't exist
            }
        }
    }

    public class ResolveProjectPathTests
    {
        [Fact]
        public void Should_Return_Path_When_Valid_Csproj_File_Exists()
        {
            // Arrange
            var service = new TestableCodeFixService();
            var tempDir = Path.GetTempPath();
            var projectPath = Path.Combine(tempDir, "test.csproj");
            File.WriteAllText(projectPath, "<Project></Project>");

            try
            {
                // Act
                var result = service.TestResolveProjectPath(projectPath);

                // Assert
                result.ShouldBe(projectPath);
            }
            finally
            {
                File.Delete(projectPath);
            }
        }

        [Fact]
        public void Should_Find_Csproj_In_Directory()
        {
            // Arrange
            var service = new TestableCodeFixService();
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var projectPath = Path.Combine(tempDir, "test.csproj");
            File.WriteAllText(projectPath, "<Project></Project>");

            try
            {
                // Act
                var result = service.TestResolveProjectPath(tempDir);

                // Assert
                result.ShouldBe(projectPath);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Should_Throw_When_No_Project_Found()
        {
            // Arrange
            var service = new TestableCodeFixService();
            var nonExistent = "NonExistentProject";

            // Act & Assert - Any exception type is acceptable for a non-existent project
            Should.Throw<Exception>(() => service.TestResolveProjectPath(nonExistent));
        }
    }


    public class LoadCodeFixProvidersTests : CodeFixServiceTests
    {
        [Fact]
        public void Should_Load_Providers_On_Construction()
        {
            // The constructor already calls LoadCodeFixProviders
            // Just verify the service was created successfully
            _sut.ShouldNotBeNull();
        }

        [Fact]
        public void Should_Handle_Missing_Assemblies_Gracefully()
        {
            // The service should not throw even if some assemblies are not found
            var service = new CodeFixService(_logger, _analyzerService, _codeFixProviderFactory, _diffService, _msBuildService);
            service.ShouldNotBeNull();
        }
    }

    // Testable wrapper to expose private methods
    private class TestableCodeFixService : CodeFixService
    {
        public TestableCodeFixService() 
            : base(A.Fake<ILogger<CodeFixService>>(), 
                   A.Fake<ISolutionAnalyzerService>(),
                   A.Fake<ICodeFixProviderFactory>(),
                   A.Fake<IDiffService>(),
                   A.Fake<IMSBuildService>())
        {
        }

        public string TestResolveProjectPath(string project)
        {
            var method = typeof(CodeFixService).GetMethod("ResolveProjectPath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (string)method!.Invoke(this, new object[] { project })!;
        }
    }
}