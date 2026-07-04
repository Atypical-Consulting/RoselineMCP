using FakeItEasy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// End-to-end proof that analyzer-driven diagnostics are real: a project on disk with a
/// Roslynator-detectable issue is loaded through a real <see cref="MSBuildService"/>, the issue
/// surfaces through <see cref="SolutionAnalyzerService.ListDiagnosticsAsync"/> (including as a
/// suggested fixable ID), and <see cref="CodeFixService.ApplyFixesAsync"/> actually fixes it on
/// disk with the bundled Roslynator fixer. Uses RCS1104 ("Simplify conditional expression",
/// enabled by default, pure syntax): <c>value == null ? true : false</c> → <c>value == null</c>.
/// Before analyzers were executed (and the Roslynator assemblies bundled), both calls were
/// blind to every RCS diagnostic — this pipeline could never work.
/// </summary>
public class AnalyzerDiagnosticsEndToEndTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly SolutionAnalyzerService _analyzerService;
    private readonly CodeFixService _codeFixService;

    public AnalyzerDiagnosticsEndToEndTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"AnalyzerDiagnosticsE2E_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        // Production wiring: bundled analyzer catalog + analyzer-aware diagnostic computation
        // (RunAnalyzers defaults to true) + catalog-scanning code fix provider factory.
        var catalog = new AnalyzerCatalog(A.Fake<ILogger<AnalyzerCatalog>>());
        var computation = new DiagnosticComputationService(
            A.Fake<ILogger<DiagnosticComputationService>>(),
            Options.Create(new RoselineMcpOptions()),
            catalog);
        var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>(), catalog);
        var filterService = new DiagnosticFilterService(factory);
        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        var projectLoader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuildService);

        _analyzerService = new SolutionAnalyzerService(
            A.Fake<ILogger<SolutionAnalyzerService>>(), msBuildService, filterService, projectLoader, computation);
        _codeFixService = new CodeFixService(
            A.Fake<ILogger<CodeFixService>>(), _analyzerService, factory,
            new DiffService(), projectLoader, computation);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, true); } catch { /* ignored */ }
    }

    private const string MinimalCsprojXml =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string SourceWithRcs1104 =
        """
        public class Conditional
        {
            public bool IsNull(object? value)
            {
                return value == null ? true : false;
            }
        }
        """;

    private string CreateProject(string projectFileName, params (string FileName, string Content)[] files)
    {
        var projectDir = Path.Combine(_testDirectory, Path.GetFileNameWithoutExtension(projectFileName));
        Directory.CreateDirectory(projectDir);

        var csprojPath = Path.Combine(projectDir, projectFileName);
        File.WriteAllText(csprojPath, MinimalCsprojXml);

        foreach (var (fileName, content) in files)
        {
            File.WriteAllText(Path.Combine(projectDir, fileName), content);
        }

        return csprojPath;
    }

    [Fact]
    public async Task ListDiagnostics_Should_Surface_Roslynator_Diagnostics_And_Mark_Them_Fixable()
    {
        // Arrange
        var csprojPath = CreateProject("RoslynatorList.csproj", ("Conditional.cs", SourceWithRcs1104));

        // Act
        var result = await _analyzerService.ListDiagnosticsAsync(csprojPath, ids: ["RCS1104"]);

        // Assert — the RCS diagnostic is reported with a real source location, counted in the
        // stats, and advertised as fixable (the bundled Roslynator fixer was discovered).
        result.TotalDiagnostics.ShouldBeGreaterThanOrEqualTo(1);
        result.Diagnostics.ShouldContain(d => d.Id == "RCS1104" && d.File.EndsWith("Conditional.cs"));
        result.Stats.ShouldNotBeNull();
        result.Stats!.ById.ShouldContainKey("RCS1104");
        result.SuggestedFixableIds.ShouldContain("RCS1104");
    }

    [Fact]
    public async Task ApplyFixes_Should_Fix_A_Roslynator_Diagnostic_On_Disk()
    {
        // Arrange
        var csprojPath = CreateProject("RoslynatorFix.csproj", ("Conditional.cs", SourceWithRcs1104));
        var sourcePath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Conditional.cs");

        // Act
        var result = await _codeFixService.ApplyFixesAsync(csprojPath, ["RCS1104"], previewOnly: false);

        // Assert — the Roslynator fixer ran and simplified the conditional on disk.
        result.FixedCount.ShouldBe(1);
        result.FixersApplied.ShouldContain("RCS1104");
        result.ChangedFiles.ShouldContain("Conditional.cs");
        result.Patch.ShouldNotBeNullOrWhiteSpace();

        var onDisk = await File.ReadAllTextAsync(sourcePath);
        onDisk.ShouldNotContain("? true : false");
        onDisk.ShouldContain("return value == null;");
    }

    [Fact]
    public async Task ApplyFixes_Should_Not_See_Roslynator_Diagnostics_When_RunAnalyzers_Is_Disabled()
    {
        // Arrange — same service graph, but RunAnalyzers=false: the fixer for RCS1104 is still
        // registered, but no RCS diagnostic is ever computed, so there is nothing to fix.
        var catalog = new AnalyzerCatalog(A.Fake<ILogger<AnalyzerCatalog>>());
        var compilerOnlyComputation = new DiagnosticComputationService(
            A.Fake<ILogger<DiagnosticComputationService>>(),
            Options.Create(new RoselineMcpOptions { RunAnalyzers = false }),
            catalog);
        var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>(), catalog);
        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        var projectLoader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuildService);
        var codeFixService = new CodeFixService(
            A.Fake<ILogger<CodeFixService>>(), _analyzerService, factory,
            new DiffService(), projectLoader, compilerOnlyComputation);

        var csprojPath = CreateProject("AnalyzersOff.csproj", ("Conditional.cs", SourceWithRcs1104));
        var sourcePath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Conditional.cs");
        var originalContent = await File.ReadAllTextAsync(sourcePath);

        // Act
        var result = await codeFixService.ApplyFixesAsync(csprojPath, ["RCS1104"], previewOnly: false);

        // Assert
        result.FixedCount.ShouldBe(0);
        result.Notes.ShouldContain(n => n.Contains("No diagnostics found for RCS1104"));
        (await File.ReadAllTextAsync(sourcePath)).ShouldBe(originalContent);
    }
}
