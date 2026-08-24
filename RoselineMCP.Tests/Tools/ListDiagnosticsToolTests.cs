using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using RoselineMCP.Configuration;
using RoselineMCP.Models;
using RoselineMCP.Services;
using RoselineMCP.Tests.Services;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

/// <summary>
/// The <c>analyzerLoad</c> block on the three diagnostics responses (#183): present — and naming
/// something — whenever an analyzer reference contributed nothing, present with zero references
/// consulted when the analyzer pass did not run, and <b>absent</b> from the wire when every
/// consulted reference contributed. An absent block means "nothing to report"; a present one
/// always says something.
/// </summary>
public class ListDiagnosticsToolTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;

    private static (SolutionAnalyzerService Analyzer, CodeFixService Fixer) ProductionServices(bool runAnalyzers = true)
    {
        var catalog = new AnalyzerCatalog(A.Fake<ILogger<AnalyzerCatalog>>());
        var computation = new DiagnosticComputationService(
            A.Fake<ILogger<DiagnosticComputationService>>(),
            Options.Create(new RoselineMcpOptions { RunAnalyzers = runAnalyzers }),
            catalog);
        var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>(), catalog);
        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        var projectLoader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuildService);
        var analyzer = new SolutionAnalyzerService(
            A.Fake<ILogger<SolutionAnalyzerService>>(), msBuildService, new DiagnosticFilterService(factory),
            projectLoader, computation);
        var fixer = new CodeFixService(
            A.Fake<ILogger<CodeFixService>>(), analyzer, factory, new DiffService(), projectLoader,
            TestVerification.New(), computation);
        return (analyzer, fixer);
    }

    [Fact]
    public async Task ListDiagnostics_Should_Report_AnalyzerLoad_For_This_Repository()
    {
        // Arrange — production wiring against this repository's own project, whose reference set
        // carries assemblies that contribute no C# analyzer (AnalyzerReferenceLoadTests).
        var (analyzer, _) = ProductionServices();

        // Act
        var result = await ListDiagnosticsTool.ListDiagnostics(
            analyzer, AnalyzerReferenceLoadTests.FindRepositoryProject(), max: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Ok.ShouldBeTrue(result.Error?.Message);
        var report = result.Data!.AnalyzerLoad.ShouldNotBeNull("at least one reference contributed nothing");
        report.ReferencesConsulted.ShouldBeGreaterThan(report.ReferencesContributing);
        report.Notes.ShouldNotBeEmpty();
        report.Notes.ShouldAllBe(n => !string.IsNullOrWhiteSpace(n.Reference) && !string.IsNullOrWhiteSpace(n.Reason));

        var json = JsonNode.Parse(JsonSerializer.Serialize(result, Wire))!.AsObject();
        json["data"]!["analyzerLoad"]!["notes"]!.AsArray().Count.ShouldBe(report.Notes.Count);
    }

    [Fact]
    public async Task ListDiagnostics_Should_Report_Zero_References_Consulted_When_Analyzers_Are_Off()
    {
        // Arrange — the block must distinguish "off" from "all fine", so it is present here.
        var (_, project) = AdhocProjectBuilder.Create("Off", [("Widget.cs", "public class Widget { }")]);
        project = project.AddAnalyzerReference(
            new AnalyzerImageReference(ImmutableArray<DiagnosticAnalyzer>.Empty, display: "Silent"));
        var loader = AdhocProjectBuilder.FakeLoaderFor((AdhocWorkspace)project.Solution.Workspace, project);
        var catalog = A.Fake<Interfaces.IAnalyzerCatalog>();
        var computation = new DiagnosticComputationService(
            A.Fake<ILogger<DiagnosticComputationService>>(),
            Options.Create(new RoselineMcpOptions { RunAnalyzers = false }),
            catalog);
        var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
        var analyzer = new SolutionAnalyzerService(
            A.Fake<ILogger<SolutionAnalyzerService>>(), A.Fake<Interfaces.IMSBuildService>(),
            new DiagnosticFilterService(factory), loader, computation);

        // Act
        var response = await analyzer.ListDiagnosticsAsync("Off", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var report = response.AnalyzerLoad.ShouldNotBeNull();
        report.AnalyzersRan.ShouldBeFalse();
        report.ReferencesConsulted.ShouldBe(0);
        report.Notes.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListDiagnostics_Should_Omit_AnalyzerLoad_When_Every_Reference_Contributed()
    {
        // Arrange — one reference, and it loads: nothing to report.
        var (_, project) = AdhocProjectBuilder.Create("Clean", [("Widget.cs", "public class Widget { }")]);
        project = project.AddAnalyzerReference(
            new AnalyzerImageReference([new QuietAnalyzer()], display: "Healthy"));
        var loader = AdhocProjectBuilder.FakeLoaderFor((AdhocWorkspace)project.Solution.Workspace, project);
        var catalog = A.Fake<Interfaces.IAnalyzerCatalog>();
        A.CallTo(() => catalog.Analyzers).Returns(ImmutableArray<DiagnosticAnalyzer>.Empty);
        var computation = new DiagnosticComputationService(
            A.Fake<ILogger<DiagnosticComputationService>>(), Options.Create(new RoselineMcpOptions()), catalog);
        var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
        var analyzer = new SolutionAnalyzerService(
            A.Fake<ILogger<SolutionAnalyzerService>>(), A.Fake<Interfaces.IMSBuildService>(),
            new DiagnosticFilterService(factory), loader, computation);

        // Act
        var response = await analyzer.ListDiagnosticsAsync("Clean", cancellationToken: TestContext.Current.CancellationToken);

        // Assert — silent on the wire.
        response.AnalyzerLoad.ShouldBeNull();
        JsonNode.Parse(JsonSerializer.Serialize(response, Wire))!.AsObject().ContainsKey("analyzerLoad").ShouldBeFalse();
    }

    [Fact]
    public async Task ApplyFixes_Should_Carry_AnalyzerLoad_From_Its_Diagnostics_Pass()
    {
        // Arrange — a fixable compiler diagnostic (so a diagnostics pass actually runs) next to a
        // reference that contributes nothing.
        var (_, project) = AdhocProjectBuilder.Create("Fixable",
            [("Widget.cs", "public class Widget { public void M() { int unused = 1; } }")]);
        project = project.AddAnalyzerReference(
            new AnalyzerImageReference(ImmutableArray<DiagnosticAnalyzer>.Empty, display: "Silent"));
        var loader = AdhocProjectBuilder.FakeLoaderFor((AdhocWorkspace)project.Solution.Workspace, project);
        var catalog = A.Fake<Interfaces.IAnalyzerCatalog>();
        A.CallTo(() => catalog.Analyzers).Returns(ImmutableArray<DiagnosticAnalyzer>.Empty);
        var computation = new DiagnosticComputationService(
            A.Fake<ILogger<DiagnosticComputationService>>(), Options.Create(new RoselineMcpOptions()), catalog);
        var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
        var fixer = new CodeFixService(
            A.Fake<ILogger<CodeFixService>>(), A.Fake<Interfaces.ISolutionAnalyzerService>(), factory,
            new DiffService(), loader, TestVerification.New(), computation);

        // Act — preview only: nothing is written, the report is still produced.
        var response = await fixer.ApplyFixesAsync("Fixable", ["CS0219"], cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var report = response.AnalyzerLoad.ShouldNotBeNull();
        report.ReferencesConsulted.ShouldBe(1);
        report.Notes.ShouldHaveSingleItem().Reference.ShouldBe("Silent");
    }

    [Fact]
    public async Task ApplyFixes_Should_Describe_AnalyzerLoad_Even_When_No_Requested_Id_Has_A_Fixer()
    {
        // Arrange — the headline #183 shape: the reference that carries both the analyzer and
        // its fixer is the one that cannot be loaded, so no provider resolves, no diagnostics
        // pass runs, and "No code fix provider found" would otherwise be the whole answer.
        var garbage = Path.Combine(Path.GetTempPath(), $"roseline-{Guid.NewGuid():N}.dll");
        File.WriteAllText(garbage, "not a PE image");
        try
        {
            var (_, project) = AdhocProjectBuilder.Create("Unfixable", [("Widget.cs", "public class Widget { }")]);
            project = project.AddAnalyzerReference(new AnalyzerFileReference(garbage, TestAnalyzerAssemblyLoader.Instance));
            var loader = AdhocProjectBuilder.FakeLoaderFor((AdhocWorkspace)project.Solution.Workspace, project);
            var catalog = A.Fake<Interfaces.IAnalyzerCatalog>();
            A.CallTo(() => catalog.Analyzers).Returns(ImmutableArray<DiagnosticAnalyzer>.Empty);
            var computation = new DiagnosticComputationService(
                A.Fake<ILogger<DiagnosticComputationService>>(), Options.Create(new RoselineMcpOptions()), catalog);
            var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
            var fixer = new CodeFixService(
                A.Fake<ILogger<CodeFixService>>(), A.Fake<Interfaces.ISolutionAnalyzerService>(), factory,
                new DiffService(), loader, TestVerification.New(), computation);

            // Act
            var response = await fixer.ApplyFixesAsync("Unfixable", ["CA9999"], cancellationToken: TestContext.Current.CancellationToken);

            // Assert — the note tells the caller why no fixer could exist.
            response.Notes.ShouldContain(n => n.Contains("No code fix provider found for CA9999"));
            var report = response.AnalyzerLoad.ShouldNotBeNull();
            report.ReferencesConsulted.ShouldBe(1);
            report.Notes.ShouldHaveSingleItem().Reason.ShouldBe(AnalyzerLoadNote.LoadFailure);
        }
        finally
        {
            File.Delete(garbage);
        }
    }

    [Fact]
    public void AnalyzerLoad_Is_Absent_From_The_Wire_On_All_Three_Responses_When_Null()
    {
        // Arrange — the clean case, constructed directly on the models.
        var list = JsonNode.Parse(JsonSerializer.Serialize(new ListDiagnosticsResponse(), Wire))!.AsObject();
        var analyze = JsonNode.Parse(JsonSerializer.Serialize(new AnalyzeSolutionResponse(), Wire))!.AsObject();
        var apply = JsonNode.Parse(JsonSerializer.Serialize(new ApplyFixesResponse(), Wire))!.AsObject();

        // Assert
        list.ContainsKey("analyzerLoad").ShouldBeFalse();
        analyze.ContainsKey("analyzerLoad").ShouldBeFalse();
        apply.ContainsKey("analyzerLoad").ShouldBeFalse();
    }

    [Fact]
    public void AnalyzerLoad_Is_Serialized_As_analyzerLoad_When_Present()
    {
        // Arrange
        var report = new AnalyzerLoadReport
        {
            ReferencesConsulted = 2,
            ReferencesContributing = 1,
            AnalyzersLoaded = 3,
            Notes = [new AnalyzerLoadNote { Reference = "Silent", Reason = AnalyzerLoadNote.NoCSharpAnalyzers }]
        };

        // Act
        var list = JsonNode.Parse(JsonSerializer.Serialize(new ListDiagnosticsResponse { AnalyzerLoad = report }, Wire))!.AsObject();
        var analyze = JsonNode.Parse(JsonSerializer.Serialize(new AnalyzeSolutionResponse { AnalyzerLoad = report }, Wire))!.AsObject();
        var apply = JsonNode.Parse(JsonSerializer.Serialize(new ApplyFixesResponse { AnalyzerLoad = report }, Wire))!.AsObject();

        // Assert
        foreach (var json in new[] { list, analyze, apply })
        {
            json["analyzerLoad"]!["referencesConsulted"]!.GetValue<int>().ShouldBe(2);
            json["analyzerLoad"]!["notes"]![0]!["reference"]!.GetValue<string>().ShouldBe("Silent");
        }
    }

    /// <summary>An analyzer that reports nothing — present only so its reference "contributes".</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class QuietAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Descriptor = new(
            "TEST9004", "Never reported", "Never reported", "Testing",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Descriptor];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
        }
    }
}
