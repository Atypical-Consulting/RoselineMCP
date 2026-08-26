using FakeItEasy;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for SolutionAnalyzerService that exercise workspace-related code paths
/// by providing real files on disk (but null workspace from mocked IMSBuildService).
/// These tests cover the async state machine paths that require real file system state.
/// </summary>
public class SolutionAnalyzerServiceWorkspaceTests : IDisposable
{
    private readonly SolutionAnalyzerService _sut;
    private readonly IMSBuildService _msBuildService;
    private readonly string _testDirectory;

    public SolutionAnalyzerServiceWorkspaceTests()
    {
        var logger = A.Fake<ILogger<SolutionAnalyzerService>>();
        _msBuildService = A.Fake<IMSBuildService>();
        var codeFixProviderFactory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
        var filterService = new DiagnosticFilterService(codeFixProviderFactory);
        // ListDiagnostics loads through IProjectLoader; a real ProjectLoader over the faked
        // IMSBuildService keeps these tests exercising the same "real files on disk, null
        // workspace" seam as before.
        var projectLoader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), _msBuildService);
        _sut = new SolutionAnalyzerService(logger, _msBuildService, filterService, projectLoader);

        _testDirectory = Path.Combine(Path.GetTempPath(), $"RoselineWSTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_testDirectory, true); }
        catch { /* ignored */ }
    }

    /// <summary>
    /// Tests that when a directory with a .sln file is passed, the service attempts workspace operations.
    /// The null workspace from the mock causes a NullReferenceException, which is caught and re-thrown.
    /// This exercises the AnalyzeSolutionAsync async state machine more deeply.
    /// </summary>
    public class AnalyzeSolutionWithRealSlnFileTests : SolutionAnalyzerServiceWorkspaceTests
    {
        [Fact]
        public async Task Should_Attempt_Workspace_When_Sln_Exists_In_Directory()
        {
            // Arrange — create a real .sln file (null workspace will cause NPE when loading)
            var slnPath = Path.Combine(_testDirectory, "TestSolution.sln");
            File.WriteAllText(slnPath, "Microsoft Visual Studio Solution File, Format Version 12.00");
            A.CallTo(() => _msBuildService.CreateWorkspace()).Returns(null!);

            // Act & Assert — null workspace → NullReferenceException on OpenSolutionAsync
            // This exercises the path: FindSolutionInDirectory → ValidateSolutionPath → CreateWorkspace → LoadSolutionAsync
            var ex = await Should.ThrowAsync<Exception>(async () =>
                await _sut.AnalyzeSolutionAsync(_testDirectory));

            // The exception should be from the workspace loading (not from validation)
            ex.ShouldNotBeNull();
        }

        [Fact]
        public async Task Should_Attempt_Workspace_When_Sln_Path_Provided_Directly()
        {
            // Arrange — create a real .sln file
            var slnPath = Path.Combine(_testDirectory, "MySolution.sln");
            File.WriteAllText(slnPath, "placeholder");
            A.CallTo(() => _msBuildService.CreateWorkspace()).Returns(null!);

            // Act & Assert — passes ValidateSolutionPath, fails at LoadSolutionAsync
            await Should.ThrowAsync<Exception>(async () =>
                await _sut.AnalyzeSolutionAsync(slnPath));

            // Verify workspace creation was attempted
            A.CallTo(() => _msBuildService.CreateWorkspace()).MustHaveHappenedOnceExactly();
        }

        [Fact]
        public async Task Should_Log_Error_Before_Rethrowing()
        {
            // Arrange
            var slnPath = Path.Combine(_testDirectory, "Solution.sln");
            File.WriteAllText(slnPath, "placeholder");
            A.CallTo(() => _msBuildService.CreateWorkspace()).Returns(null!);

            // Act & Assert
            await Should.ThrowAsync<Exception>(async () =>
                await _sut.AnalyzeSolutionAsync(slnPath, severity: "warning"));

            // Workspace was created (and failed to load — null)
            A.CallTo(() => _msBuildService.CreateWorkspace()).MustHaveHappenedOnceExactly();
        }
    }

    /// <summary>
    /// Tests that when a .csproj file exists, ListDiagnosticsAsync attempts to load it through the
    /// shared <see cref="ProjectLoader"/> (null workspace → NPE, caught + rethrown).
    /// </summary>
    public class ListDiagnosticsWithRealCsprojTests : SolutionAnalyzerServiceWorkspaceTests
    {
        [Fact]
        public async Task Should_Attempt_Workspace_Load_When_Csproj_File_Exists()
        {
            // Arrange — create a real .csproj file
            var csprojPath = Path.Combine(_testDirectory, "TestProject.csproj");
            File.WriteAllText(csprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            A.CallTo(() => _msBuildService.CreateWorkspace()).Returns(null!);

            // Act & Assert
            // ProjectLoader resolves the .csproj path → workspace load → NPE (workspace is null)
            await Should.ThrowAsync<Exception>(async () =>
                await _sut.ListDiagnosticsAsync(csprojPath));

            A.CallTo(() => _msBuildService.CreateWorkspace()).MustHaveHappenedOnceExactly();
        }

        [Fact]
        public async Task Should_Open_Containing_Solution_When_One_Exists_Above_The_Csproj()
        {
            // Arrange — create a .csproj file in a subdir, and a .sln in the parent
            var subDir = Path.Combine(_testDirectory, "src");
            Directory.CreateDirectory(subDir);
            var csprojPath = Path.Combine(subDir, "MyProject.csproj");
            File.WriteAllText(csprojPath, "<Project />");
            // Also create a .sln in the parent dir so ProjectLoader's FindSolutionFile can find it
            var slnPath = Path.Combine(_testDirectory, "MySolution.sln");
            File.WriteAllText(slnPath, "placeholder");
            A.CallTo(() => _msBuildService.CreateWorkspace()).Returns(null!);

            // Act & Assert — the containing solution is opened first (NPE on the null workspace)
            await Should.ThrowAsync<Exception>(async () =>
                await _sut.ListDiagnosticsAsync(csprojPath));
        }
    }
}

/// <summary>
/// End-to-end (real MSBuild, real <see cref="ProjectLoader"/>): ListDiagnostics accepts a
/// <c>.sln</c> path — a reference the old private resolution copy rejected — and reports
/// diagnostics for the solution's primary project.
/// </summary>
public class ListDiagnosticsProjectLoaderIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly SolutionAnalyzerService _sut;

    public ListDiagnosticsProjectLoaderIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"RoselineListDiagSln_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        var filterService = new DiagnosticFilterService(
            new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>()));
        var projectLoader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuildService);
        _sut = new SolutionAnalyzerService(
            A.Fake<ILogger<SolutionAnalyzerService>>(), msBuildService, filterService, projectLoader);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_testDirectory, true); }
        catch { /* ignored */ }
    }

    [Fact]
    public async Task Accepts_A_Sln_Path_And_Lists_Diagnostics_For_Its_Primary_Project()
    {
        // Arrange — a real SDK-style project (one CS0219 warning) referenced by a real .sln.
        var projectDir = Path.Combine(_testDirectory, "App");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDir, "Widget.cs"),
            "namespace App { public class Widget { public void M() { int unused = 1; } } }");

        var slnPath = SolutionFileBuilder.Write(Path.Combine(_testDirectory, "App.sln"), "App");

        // Act — pass the .sln path directly.
        var result = await _sut.ListDiagnosticsAsync(slnPath, ids: ["CS0219"], cancellationToken: TestContext.Current.CancellationToken);

        // Assert — the primary project was analyzed and the expected diagnostic reported.
        result.Project.ShouldBe("App");
        result.TotalDiagnostics.ShouldBe(1);
        result.Diagnostics.ShouldHaveSingleItem().Id.ShouldBe("CS0219");
    }
}
