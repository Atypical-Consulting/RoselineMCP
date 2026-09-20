using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// End-to-end proof for #242: a project carrying an <c>&lt;Analyzer Include&gt;</c> whose assembly
/// is not on disk must still answer every relationship query and every rename.
/// </summary>
/// <remarks>
/// <para>
/// MSBuild resolves such an item to <c>UnresolvedAnalyzerReference</c>, a sentinel Roslyn's own
/// <c>SerializerService.CreateChecksum</c> refuses — it throws
/// <c>Unexpected value '…UnresolvedAnalyzerReference'</c>. That checksum is reached from
/// <c>ProjectState.GetChecksumAsync</c> via <c>DependentTypeFinder.ProjectIndex</c>, so
/// <c>find_references</c>, <c>find_implementations</c>, <c>get_call_graph</c>,
/// <c>get_type_hierarchy</c> and <c>rename_symbol</c> all aborted outright — 16% of the four
/// navigation tools' traffic on one real solution.
/// </para>
/// <para>
/// The fixture is a minimal SDK-style project written to a temp directory and design-time-built
/// <b>without</b> a prior <c>dotnet restore</c> — the established pattern in this suite (see
/// <see cref="CodeFixServiceIntegrationTests"/>), and what keeps the three CI OS legs identical.
/// </para>
/// </remarks>
public class UnresolvedAnalyzerReferenceTests : IDisposable
{
    private const string MissingAnalyzerFileName = "Missing.Analyzer.dll";

    private readonly string _testDirectory;
    private readonly ProjectLoader _loader;
    private readonly CodeNavigationService _navigation;
    private readonly CodeEditService _edit;
    private readonly CodeFixService _codeFix;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public UnresolvedAnalyzerReferenceTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"UnresolvedAnalyzerRef_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        _loader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuildService);
        _navigation = new CodeNavigationService(A.Fake<ILogger<CodeNavigationService>>(), _loader);
        _edit = new CodeEditService(
            A.Fake<ILogger<CodeEditService>>(), _loader, new DiffService(), TestVerification.New());

        var catalog = new AnalyzerCatalog(A.Fake<ILogger<AnalyzerCatalog>>());
        var computation = new DiagnosticComputationService(
            A.Fake<ILogger<DiagnosticComputationService>>(),
            Options.Create(new RoselineMcpOptions()),
            catalog);
        var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>(), catalog);
        var analyzerService = new SolutionAnalyzerService(
            A.Fake<ILogger<SolutionAnalyzerService>>(), msBuildService,
            new DiagnosticFilterService(factory), _loader, computation);
        _codeFix = new CodeFixService(
            A.Fake<ILogger<CodeFixService>>(), analyzerService, factory,
            new DiffService(), _loader, TestVerification.New(), computation);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, true); } catch { /* ignored */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A project whose only unusual property is one analyzer item pointing at a dll that does not
    /// exist — the exact shape a git worktree with a stale <c>obj/</c> produces.
    /// </summary>
    private const string CsprojWithMissingAnalyzer =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Library</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <Analyzer Include="$(MSBuildThisFileDirectory)nonexistent/Missing.Analyzer.dll" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>
    /// An interface, an implementation, an override and a caller — one of each relationship the four
    /// navigation tools traverse. <c>Unused</c> raises CS0219, the diagnostic every case in
    /// <see cref="CodeFixServiceIntegrationTests"/> already drives, so <c>apply_fixes</c> can be
    /// exercised over the same fixture.
    /// </summary>
    private const string FixtureSource =
        """
        namespace App;

        public interface IThing
        {
            void Do();
        }

        public class Base : IThing
        {
            public virtual void Do() { }
        }

        public class Derived : Base
        {
            public override void Do() { }
        }

        public static class Caller
        {
            public static void Run()
            {
                IThing thing = new Derived();
                thing.Do();
            }

            public static void Unused() { var unused = 1; }
        }
        """;

    private string CreateFixtureProject()
    {
        var projectDir = Path.Combine(_testDirectory, "App");
        Directory.CreateDirectory(projectDir);
        var csproj = Path.Combine(projectDir, "App.csproj");
        File.WriteAllText(csproj, CsprojWithMissingAnalyzer);
        File.WriteAllText(Path.Combine(projectDir, "App.cs"), FixtureSource);
        return csproj;
    }

    [Fact]
    public async Task The_Loader_Reports_The_Missing_Analyzer_Reference_It_Removed()
    {
        var csproj = CreateFixtureProject();

        using var loaded = await _loader.LoadAsync(csproj, Ct);

        loaded.UnresolvedAnalyzerReferences.ShouldNotBeEmpty(
            "the fixture's <Analyzer Include> names a dll that does not exist");
        loaded.UnresolvedAnalyzerReferences.ShouldContain(p => p.EndsWith(MissingAnalyzerFileName, StringComparison.Ordinal));
        loaded.Project.AnalyzerReferences.Any(r => r is UnresolvedAnalyzerReference).ShouldBeFalse(
            "the sentinel Roslyn cannot checksum must be gone from the handed-out solution");
    }

    [Fact]
    public async Task Find_References_Answers_Instead_Of_Throwing()
    {
        var csproj = CreateFixtureProject();

        var response = await _navigation.FindReferencesAsync(csproj, "IThing.Do", includeDefinition: false, max: 50, Ct);

        response.References.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Find_Implementations_Answers_Instead_Of_Throwing()
    {
        var csproj = CreateFixtureProject();

        var response = await _navigation.FindImplementationsAsync(csproj, "IThing", max: 50, Ct);

        response.Implementations.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Get_Call_Graph_Answers_Instead_Of_Throwing()
    {
        var csproj = CreateFixtureProject();

        var response = await _navigation.GetCallGraphAsync(csproj, "Caller.Run", "both", depth: 2, max: 50, Ct);

        response.Callees.ShouldNotBeNull().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Get_Type_Hierarchy_Answers_Instead_Of_Throwing()
    {
        var csproj = CreateFixtureProject();

        var response = await _navigation.GetTypeHierarchyAsync(csproj, "Base", "both", max: 50, Ct);

        response.DerivedTypes.ShouldNotBeNull().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Rename_Symbol_Previews_Instead_Of_Throwing()
    {
        var csproj = CreateFixtureProject();

        var response = await _edit.RenameSymbolAsync(csproj, "Derived", "Renamed", previewOnly: true, cancellationToken: Ct);

        response.Patch.ShouldNotBeNullOrWhiteSpace();
        response.ChangedFiles.ShouldNotBeEmpty();
    }

    /// <summary>
    /// The open question #242's plan left to measurement: <c>apply_fixes</c> is reached through a
    /// different path (a fixer's own <c>CodeAction</c>), so whether it hits the project-state
    /// checksum depends on the fixer. CS0219's does not call <c>Renamer</c> — so this case passes
    /// against the unstripped solution too, and is kept as a pin rather than a regression proof.
    /// </summary>
    [Fact]
    public async Task Apply_Fixes_Answers_Over_The_Same_Project()
    {
        var csproj = CreateFixtureProject();

        var response = await _codeFix.ApplyFixesAsync(csproj, ["CS0219"], previewOnly: true, cancellationToken: Ct);

        response.FixersApplied.ShouldContain("CS0219");
        response.Patch.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The regression pin. The workspace's own <c>CurrentSolution</c> is never mutated by the strip,
    /// so it <em>is</em> the unstripped solution — the exact input <c>SymbolFinder</c> used to be
    /// handed. If a future Roslyn learns to checksum <c>UnresolvedAnalyzerReference</c>, this test
    /// goes red and the strip can be retired, rather than quietly becoming dead code.
    /// </summary>
    [Fact]
    public async Task The_Unstripped_Solution_Still_Throws_So_An_Upstream_Fix_Is_Noticed()
    {
        var csproj = CreateFixtureProject();
        using var loaded = await _loader.LoadAsync(csproj, Ct);

        var unstripped = loaded.Workspace.CurrentSolution;
        unstripped.Projects
            .SelectMany(p => p.AnalyzerReferences)
            .Any(r => r is UnresolvedAnalyzerReference)
            .ShouldBeTrue("the workspace's own solution must still carry what the strip removed");

        var compilation = await unstripped.Projects.First().GetCompilationAsync(Ct);
        var thing = compilation!.GetTypeByMetadataName("App.IThing").ShouldNotBeNull();

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            async () => await SymbolFinder.FindImplementationsAsync(thing, unstripped, cancellationToken: Ct));
        ex.Message.ShouldContain("Unexpected value");
    }

    /// <summary>
    /// The cache is the second half of the contract: <c>CachingProjectLoader</c> hands out a handle
    /// built from its own <c>CacheEntry</c> on a hit, so a pass-through that only fires on the miss
    /// would report the removed references once and then silently stop.
    /// </summary>
    [Fact]
    public async Task The_Caching_Loader_Reports_The_Same_Removed_References_On_A_Hit()
    {
        var csproj = CreateFixtureProject();
        using var caching = new CachingProjectLoader(
            _loader, Options.Create(new RoselineMcpOptions()),
            A.Fake<ILogger<CachingProjectLoader>>(), _ => csproj);

        IReadOnlyList<string> onMiss;
        using (var first = await caching.LoadAsync(csproj, Ct))
        {
            onMiss = first.UnresolvedAnalyzerReferences;
        }

        using var second = await caching.LoadAsync(csproj, Ct);

        onMiss.ShouldNotBeEmpty();
        onMiss.ShouldContain(p => p.EndsWith(MissingAnalyzerFileName, StringComparison.Ordinal));
        second.UnresolvedAnalyzerReferences.ShouldBe(onMiss);
    }
}
