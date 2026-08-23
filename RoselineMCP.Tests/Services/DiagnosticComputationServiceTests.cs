using System.Collections.Immutable;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// The analyzer-load report of <see cref="DiagnosticComputationService"/> (#183): every analyzer
/// reference that contributes nothing is <b>named</b>, with Roslyn's own reason when it gave one.
/// Before this, a reference that failed to load (an analyzer built against a newer
/// <c>Microsoft.CodeAnalysis</c> than the one in-process) was indistinguishable from one that
/// genuinely has no C# analyzers: both come back as an empty array, and the pass walked on.
/// </summary>
public class DiagnosticComputationServiceTests : IDisposable
{
    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"RoselineAnalyzerLoad_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        { Directory.Delete(_tempDirectory, recursive: true); }
        catch { /* best effort */ }
    }

    private static IAnalyzerCatalog EmptyCatalog()
    {
        var catalog = A.Fake<IAnalyzerCatalog>();
        A.CallTo(() => catalog.Analyzers).Returns(ImmutableArray<DiagnosticAnalyzer>.Empty);
        return catalog;
    }

    private static DiagnosticComputationService CreateService(IAnalyzerCatalog catalog, bool runAnalyzers = true) =>
        new(
            A.Fake<ILogger<DiagnosticComputationService>>(),
            Options.Create(new RoselineMcpOptions { RunAnalyzers = runAnalyzers }),
            catalog);

    private static async Task<(Project Project, Compilation Compilation)> WidgetProjectAsync(
        params AnalyzerReference[] references)
    {
        var (_, project) = AdhocProjectBuilder.Create("Widgets", [("Widget.cs", "public class Widget { }")]);
        foreach (var reference in references)
        {
            project = project.AddAnalyzerReference(reference);
        }

        var compilation = (await project.GetCompilationAsync(TestContext.Current.CancellationToken))!;
        return (project, compilation);
    }

    private static AnalyzerFileReference FileReference(string path) => new(path, TestAnalyzerAssemblyLoader.Instance);

    /// <summary>A file with a <c>.dll</c> extension that is not a PE image at all.</summary>
    private string WriteGarbageAssembly()
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, "NotAnAssembly.dll");
        File.WriteAllText(path, "this is not a PE image");
        return path;
    }

    private static List<MetadataReference> TrustedReferences(bool includeRoslyn)
    {
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0 && (includeRoslyn || !Path.GetFileName(p).StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal)))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
    }

    private string Emit(CSharpCompilation compilation, string fileName)
    {
        Directory.CreateDirectory(_tempDirectory);
        var path = Path.Combine(_tempDirectory, fileName);
        var emit = compilation.Emit(path);
        emit.Success.ShouldBeTrue(string.Join(Environment.NewLine, emit.Diagnostics));
        return path;
    }

    /// <summary>
    /// Reproduces the shape behind #183 deterministically: an analyzer assembly compiled against a
    /// stub <c>Microsoft.CodeAnalysis</c> whose assembly version (99.0.0.0) is newer than the one
    /// loaded in this process. Its <c>DiagnosticAnalyzer</c> base type cannot resolve at runtime,
    /// exactly as the .NET SDK's own NetAnalyzers cannot when the SDK is ahead of the server's
    /// Roslyn — so <c>GetAnalyzers</c> answers with an empty array, not an exception.
    /// </summary>
    private string WriteAnalyzerBuiltAgainstANewerCompiler()
    {
        // The framework references only — the real Microsoft.CodeAnalysis.dll is in the trusted
        // set too, and it must not be visible: the stub below takes its name.
        var references = TrustedReferences(includeRoslyn: false);

        var stub = CSharpCompilation.Create(
            "Microsoft.CodeAnalysis",
            [CSharpSyntaxTree.ParseText("""
                [assembly: System.Reflection.AssemblyVersion("99.0.0.0")]
                namespace Microsoft.CodeAnalysis.Diagnostics
                {
                    public abstract class DiagnosticAnalyzer { }

                    [System.AttributeUsage(System.AttributeTargets.Class)]
                    public sealed class DiagnosticAnalyzerAttribute : System.Attribute
                    {
                        public DiagnosticAnalyzerAttribute(string firstLanguage, params string[] additionalLanguages) { }
                    }
                }
                """)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var analyzer = CSharpCompilation.Create(
            "FutureAnalyzers",
            [CSharpSyntaxTree.ParseText("""
                using Microsoft.CodeAnalysis.Diagnostics;

                [DiagnosticAnalyzer("C#")]
                public sealed class FutureAnalyzer : DiagnosticAnalyzer { }
                """)],
            [.. references, stub.ToMetadataReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return Emit(analyzer, "FutureAnalyzers.dll");
    }

    /// <summary>
    /// A real analyzer assembly (built against the in-process Roslyn) carrying two analyzers, one
    /// of which cannot be constructed: Roslyn raises <c>UnableToCreateAnalyzer</c> for it and still
    /// returns the other. The partial case — the one a naive "any failure means nothing loaded"
    /// would turn into a regression.
    /// </summary>
    private string WritePartiallyLoadableAnalyzers()
    {
        var analyzer = CSharpCompilation.Create(
            "PartialAnalyzers",
            [CSharpSyntaxTree.ParseText("""
                using System.Collections.Immutable;
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.Diagnostics;

                [DiagnosticAnalyzer("C#")]
                public sealed class HealthyAnalyzer : DiagnosticAnalyzer
                {
                    private static readonly DiagnosticDescriptor Descriptor = new(
                        "PART0001", "Healthy", "Healthy", "Testing", DiagnosticSeverity.Warning, isEnabledByDefault: true);
                    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Descriptor);
                    public override void Initialize(AnalysisContext context)
                    {
                        context.EnableConcurrentExecution();
                        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
                    }
                }

                [DiagnosticAnalyzer("C#")]
                public sealed class BrokenAnalyzer : DiagnosticAnalyzer
                {
                    static BrokenAnalyzer() { throw new System.InvalidOperationException("cannot be constructed"); }
                    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray<DiagnosticDescriptor>.Empty;
                    public override void Initialize(AnalysisContext context) { }
                }
                """)],
            TrustedReferences(includeRoslyn: true),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return Emit(analyzer, "PartialAnalyzers.dll");
    }

    /// <summary>An <see cref="AnalyzerReference"/> whose <c>GetAnalyzers</c> throws.</summary>
    private sealed class ThrowingReference : AnalyzerReference
    {
        public override string? FullPath => null;
        public override string Display => "Throwing.Reference";
        public override object Id => this;
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) =>
            throw new InvalidOperationException("deliberately broken reference");
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() =>
            throw new InvalidOperationException("deliberately broken reference");
    }

    [Fact]
    public async Task Should_Consult_Every_Reference_Of_A_Real_Project_And_Name_The_Silent_Ones()
    {
        // Arrange — this repository's own project, the way the tools load it. Some of its
        // references carry no C# analyzer (generator-only assemblies, fixer-only assemblies, an
        // analyzer's support libraries), and the report must say which — and any reference built
        // against a newer Roslyn than ours must be named as a load failure, not as "none".
        using var loaded = await AnalyzerReferenceLoadTests.LoadRepositoryProjectAsync();
        var project = loaded.Project;
        var compilation = (await project.GetCompilationAsync(TestContext.Current.CancellationToken))!;
        var service = CreateService(new AnalyzerCatalog(A.Fake<ILogger<AnalyzerCatalog>>()));

        // Act — the service consults every reference FIRST: Roslyn raises AnalyzerLoadFailed only
        // on the first consultation, so probing the references before the service would consume
        // the very event this test pins.
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // The ground truth, read after the fact (Roslyn caches each reference's answer).
        var inProcess = typeof(Diagnostic).Assembly.GetName().Version!;
        var expected = project.AnalyzerReferences
            .Where(r => r.GetAnalyzers(LanguageNames.CSharp).IsEmpty)
            .Select(r => (r.Display, Reason: r.FullPath is { } path
                && AnalyzerReferenceLoadTests.ReadReferencedRoslynVersion(path) is { } binds && binds > inProcess
                    ? AnalyzerLoadNote.LoadFailure
                    : AnalyzerLoadNote.NoCSharpAnalyzers))
            .ToList();
        expected.ShouldNotBeEmpty("the ground truth pinned by AnalyzerReferenceLoadTests");

        // Assert
        var report = result.AnalyzerLoad;
        report.AnalyzersRan.ShouldBeTrue();
        report.ReferencesConsulted.ShouldBe(project.AnalyzerReferences.Count);
        report.ReferencesContributing.ShouldBe(report.ReferencesConsulted - expected.Count);
        report.AnalyzersLoaded.ShouldBeGreaterThan(0);
        report.Notes.Select(n => (n.Reference, n.Reason)).ShouldBe(expected, ignoreOrder: true);
        result.Diagnostics.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Should_Report_That_Analyzers_Did_Not_Run_When_RunAnalyzers_Is_Disabled()
    {
        // Arrange — the project does carry a reference, but the pass is switched off.
        var (project, compilation) = await WidgetProjectAsync(
            new AnalyzerImageReference(ImmutableArray<DiagnosticAnalyzer>.Empty, display: "Unused.Reference"));
        var service = CreateService(EmptyCatalog(), runAnalyzers: false);

        // Act
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert — "off" is distinguishable from "all fine": the pass did not run, nothing named.
        result.AnalyzerLoad.AnalyzersRan.ShouldBeFalse();
        result.AnalyzerLoad.ReferencesConsulted.ShouldBe(0);
        result.AnalyzerLoad.ReferencesContributing.ShouldBe(0);
        result.AnalyzerLoad.AnalyzersLoaded.ShouldBe(0);
        result.AnalyzerLoad.Notes.ShouldBeEmpty();
        result.Diagnostics.ShouldBe(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompilerOnly_Should_Report_That_Analyzers_Did_Not_Run()
    {
        // Arrange
        var (project, compilation) = await WidgetProjectAsync(
            new AnalyzerImageReference(ImmutableArray<DiagnosticAnalyzer>.Empty, display: "Unused.Reference"));

        // Act
        var result = await DiagnosticComputationService.CompilerOnly
            .GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert
        result.AnalyzerLoad.AnalyzersRan.ShouldBeFalse();
        result.AnalyzerLoad.ReferencesConsulted.ShouldBe(0);
        result.AnalyzerLoad.Notes.ShouldBeEmpty();
        DiagnosticComputationService.CompilerOnly.DescribeAnalyzerLoad(project).AnalyzersRan.ShouldBeFalse();
        result.Diagnostics.ShouldBe(compilation.GetDiagnostics(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Should_Name_A_Reference_That_Declares_No_CSharp_Analyzers()
    {
        // Arrange — loads fine, carries nothing for C#: accurate, not alarming.
        var (project, compilation) = await WidgetProjectAsync(
            new AnalyzerImageReference(ImmutableArray<DiagnosticAnalyzer>.Empty, display: "Generators.Only"));
        var service = CreateService(EmptyCatalog());

        // Act
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert
        result.AnalyzerLoad.ReferencesConsulted.ShouldBe(1);
        result.AnalyzerLoad.ReferencesContributing.ShouldBe(0);
        var note = result.AnalyzerLoad.Notes.ShouldHaveSingleItem();
        note.Reference.ShouldBe("Generators.Only");
        note.Reason.ShouldBe(AnalyzerLoadNote.NoCSharpAnalyzers);
        note.Message.ShouldBeNull();
        note.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Should_Record_Roslyns_Diagnosis_When_A_File_Reference_Cannot_Be_Loaded()
    {
        // Arrange — Roslyn does not throw here: it raises AnalyzerLoadFailed and returns nothing.
        var reference = FileReference(WriteGarbageAssembly());
        var (project, compilation) = await WidgetProjectAsync(reference);
        var service = CreateService(EmptyCatalog());

        // Act
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert — the event, not the (absent) exception, is what names the failure.
        var note = result.AnalyzerLoad.Notes.ShouldHaveSingleItem();
        note.Reference.ShouldBe(reference.Display);
        note.Reason.ShouldBe(AnalyzerLoadNote.LoadFailure);
        note.ErrorCode.ShouldBe(nameof(AnalyzerLoadFailureEventArgs.FailureErrorCode.UnableToLoadAnalyzer));
        note.Message.ShouldNotBeNullOrWhiteSpace();
        result.AnalyzerLoad.ReferencesContributing.ShouldBe(0);
    }

    [Fact]
    public async Task Should_Name_An_Analyzer_Built_Against_A_Newer_Compiler()
    {
        // Arrange — the exact mechanism of #183: the assembly loads, its analyzer types do not,
        // because they bind a Microsoft.CodeAnalysis newer than the one in-process.
        var reference = FileReference(WriteAnalyzerBuiltAgainstANewerCompiler());
        var (project, compilation) = await WidgetProjectAsync(reference);
        var service = CreateService(EmptyCatalog());

        // Act
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert — named, classified, and the version Roslyn saw is in the message.
        var note = result.AnalyzerLoad.Notes.ShouldHaveSingleItem();
        note.Reference.ShouldBe("FutureAnalyzers");
        note.Reason.ShouldBe(AnalyzerLoadNote.LoadFailure);
        note.ErrorCode.ShouldBe(nameof(AnalyzerLoadFailureEventArgs.FailureErrorCode.ReferencesNewerCompiler));
        note.Message.ShouldNotBeNull();
        note.Message.ShouldContain("99.0.0.0");
    }

    [Fact]
    public async Task Should_Keep_The_Analyzers_That_Loaded_When_A_Reference_Loads_Only_Partially()
    {
        // Arrange — two analyzers in one assembly, one of which cannot be constructed. Before this
        // report existed the healthy one ran; naming the failure must not stop it from running.
        var reference = FileReference(WritePartiallyLoadableAnalyzers());
        var (project, compilation) = await WidgetProjectAsync(reference);
        var service = CreateService(EmptyCatalog());

        // Act
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert — the reference contributed, its healthy analyzer ran, and the failure is named
        // as partial rather than total.
        result.AnalyzerLoad.ReferencesContributing.ShouldBe(1);
        result.AnalyzerLoad.AnalyzersLoaded.ShouldBe(1);
        var note = result.AnalyzerLoad.Notes.ShouldHaveSingleItem();
        note.Reference.ShouldBe("PartialAnalyzers");
        note.Reason.ShouldBe(AnalyzerLoadNote.LoadFailure);
        note.ErrorCode.ShouldBe(nameof(AnalyzerLoadFailureEventArgs.FailureErrorCode.UnableToCreateAnalyzer));
        note.Message.ShouldNotBeNull();
        note.Message.ShouldStartWith("partial");
        note.Message.ShouldContain("1 loaded");

        // And the same answer on a second consultation, when Roslyn no longer raises the event.
        var second = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);
        second.AnalyzerLoad.ReferencesContributing.ShouldBe(1);
        second.AnalyzerLoad.Notes.ShouldHaveSingleItem().Reason.ShouldBe(AnalyzerLoadNote.LoadFailure);
    }

    [Fact]
    public async Task Should_Remember_A_Load_Failure_When_The_Same_Reference_Is_Consulted_Again()
    {
        // Arrange — the workspace cache hands the same AnalyzerFileReference to every call, and
        // Roslyn raises AnalyzerLoadFailed only the first time it tries: a second pass would
        // otherwise see an empty array with no event and misreport "no C# analyzers".
        var reference = FileReference(WriteGarbageAssembly());
        var (project, compilation) = await WidgetProjectAsync(reference);
        var service = CreateService(EmptyCatalog());
        await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Act
        var second = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert
        var note = second.AnalyzerLoad.Notes.ShouldHaveSingleItem();
        note.Reason.ShouldBe(AnalyzerLoadNote.LoadFailure);
        note.ErrorCode.ShouldBe(nameof(AnalyzerLoadFailureEventArgs.FailureErrorCode.UnableToLoadAnalyzer));
    }

    [Fact]
    public async Task Concurrent_Consultations_Of_One_Failing_Reference_Should_All_Name_The_Failure()
    {
        // Arrange — a solution's projects are analyzed in parallel and share reference objects;
        // only one consultation can observe Roslyn's one-shot event, the others must inherit it.
        var reference = FileReference(WriteGarbageAssembly());
        var (project, compilation) = await WidgetProjectAsync(reference);
        var service = CreateService(EmptyCatalog());

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken)));

        // Assert
        results.ShouldAllBe(r => r.AnalyzerLoad.Notes.Count == 1
            && r.AnalyzerLoad.Notes[0].Reason == AnalyzerLoadNote.LoadFailure);
    }

    [Fact]
    public async Task Should_Record_An_Exception_When_GetAnalyzers_Throws()
    {
        // Arrange — the one path the old code did guard; it still degrades, now with a name.
        var (project, compilation) = await WidgetProjectAsync(new ThrowingReference());
        var service = CreateService(EmptyCatalog());

        // Act
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert
        var note = result.AnalyzerLoad.Notes.ShouldHaveSingleItem();
        note.Reference.ShouldBe("Throwing.Reference");
        note.Reason.ShouldBe(AnalyzerLoadNote.Exception);
        note.Message.ShouldBe("deliberately broken reference");
        note.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Should_Count_Contributing_References_And_Loaded_Analyzers()
    {
        // Arrange — one contributing reference, one silent, plus a bundled analyzer that is also
        // referenced by the project (deduplicated: counted once in analyzersLoaded).
        var bundled = new CountingAnalyzer();
        var catalog = A.Fake<IAnalyzerCatalog>();
        A.CallTo(() => catalog.Analyzers).Returns(ImmutableArray.Create<DiagnosticAnalyzer>(bundled));
        var (project, compilation) = await WidgetProjectAsync(
            new AnalyzerImageReference([new CountingAnalyzer()], display: "Also.Bundled"),
            new AnalyzerImageReference(ImmutableArray<DiagnosticAnalyzer>.Empty, display: "Silent"));
        var service = CreateService(catalog);

        // Act
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert
        result.AnalyzerLoad.ReferencesConsulted.ShouldBe(2);
        result.AnalyzerLoad.ReferencesContributing.ShouldBe(1);
        result.AnalyzerLoad.AnalyzersLoaded.ShouldBe(1);
        result.AnalyzerLoad.Notes.ShouldHaveSingleItem().Reference.ShouldBe("Silent");
    }

    [Fact]
    public async Task Should_Report_Clean_When_Every_Reference_Contributes()
    {
        // Arrange
        var (project, compilation) = await WidgetProjectAsync(
            new AnalyzerImageReference([new CountingAnalyzer()], display: "Healthy"));
        var service = CreateService(EmptyCatalog());

        // Act
        var result = await service.GetDiagnosticsAsync(project, compilation, TestContext.Current.CancellationToken);

        // Assert — nothing to name.
        result.AnalyzerLoad.AnalyzersRan.ShouldBeTrue();
        result.AnalyzerLoad.ReferencesConsulted.ShouldBe(1);
        result.AnalyzerLoad.ReferencesContributing.ShouldBe(1);
        result.AnalyzerLoad.Notes.ShouldBeEmpty();
        result.AnalyzerLoad.HasSomethingToReport.ShouldBeFalse();
    }

    [Fact]
    public async Task DescribeAnalyzerLoad_Should_Name_The_Silent_References_Without_A_Diagnostics_Pass()
    {
        // Arrange — what apply_fixes needs when none of its IDs had a fixer: the load report, and
        // nothing else.
        var reference = FileReference(WriteGarbageAssembly());
        var (project, _) = await WidgetProjectAsync(
            reference, new AnalyzerImageReference([new CountingAnalyzer()], display: "Healthy"));
        var service = CreateService(EmptyCatalog());

        // Act
        var report = service.DescribeAnalyzerLoad(project);

        // Assert
        report.AnalyzersRan.ShouldBeTrue();
        report.ReferencesConsulted.ShouldBe(2);
        report.ReferencesContributing.ShouldBe(1);
        report.Notes.ShouldHaveSingleItem().Reason.ShouldBe(AnalyzerLoadNote.LoadFailure);
        CreateService(EmptyCatalog(), runAnalyzers: false).DescribeAnalyzerLoad(project).AnalyzersRan.ShouldBeFalse();
    }

    /// <summary>An analyzer that reports nothing — present only to be counted.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class CountingAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Descriptor = new(
            "TEST9003", "Never reported", "Never reported", "Testing",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Descriptor];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
        }
    }
}
