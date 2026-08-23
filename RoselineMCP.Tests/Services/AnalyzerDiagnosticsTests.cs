using System.Collections.Immutable;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Coverage for the analyzer-execution pipeline that makes analyzer-driven diagnostics (RCS*)
/// real: <see cref="AnalyzerCatalog"/> loads the Roslynator assemblies bundled into the
/// <c>analyzers/</c> folder next to RoselineMCP.dll, and
/// <see cref="DiagnosticComputationService"/> runs them (plus a project's own analyzer
/// references) via <c>CompilationWithAnalyzers</c> on top of compiler diagnostics.
/// Historically none of this existed: every diagnostics path called
/// <c>compilation.GetDiagnostics()</c> only, so RCS/CA/custom-analyzer diagnostics could never
/// appear anywhere, despite being advertised.
/// </summary>
public class AnalyzerDiagnosticsTests
{
    private static AnalyzerCatalog CreateCatalog() =>
        new(A.Fake<ILogger<AnalyzerCatalog>>());

    private static DiagnosticComputationService CreateComputationService(
        IAnalyzerCatalog catalog, bool runAnalyzers = true) =>
        new(
            A.Fake<ILogger<DiagnosticComputationService>>(),
            Options.Create(new RoselineMcpOptions { RunAnalyzers = runAnalyzers }),
            catalog);

    /// <summary>Source with a deterministic, pure-syntax Roslynator finding: RCS1104
    /// ("Simplify conditional expression") on <c>value == null ? true : false</c>.</summary>
    private const string SourceWithRcs1104 =
        """
        public class Conditional
        {
            public bool IsNull(object value)
            {
                return value == null ? true : false;
            }
        }
        """;

    public class AnalyzerCatalogTests : AnalyzerDiagnosticsTests
    {
        [Fact]
        public void Should_Load_Bundled_Roslynator_Assemblies()
        {
            // Arrange & Act
            var catalog = CreateCatalog();

            // Assert — the analyzers/ folder flows into the test output via the project
            // reference, exactly like it flows into the packed tool and publish output.
            catalog.Assemblies.ShouldNotBeEmpty();
            catalog.Assemblies.ShouldContain(a => a.GetName().Name!.StartsWith("Roslynator"));
        }

        [Fact]
        public void Should_Instantiate_CSharp_Analyzers_Covering_RCS_Rules()
        {
            // Arrange & Act
            var catalog = CreateCatalog();

            // Assert
            catalog.Analyzers.ShouldNotBeEmpty();
            var supportedIds = catalog.Analyzers
                .SelectMany(a => a.SupportedDiagnostics)
                .Select(d => d.Id)
                .ToHashSet();
            supportedIds.ShouldContain("RCS1104"); // Simplify conditional expression
            supportedIds.ShouldContain("RCS1036"); // Remove unnecessary blank line
        }

        [Fact]
        public void Should_Load_Once_And_Return_Stable_Instances()
        {
            // Arrange
            var catalog = CreateCatalog();

            // Act & Assert — lazy, cached loading: repeated access yields the same arrays.
            catalog.Analyzers.ShouldBe(catalog.Analyzers);
            catalog.Assemblies.ShouldBeSameAs(catalog.Assemblies);
        }
    }

    public class CodeFixProviderFactoryWithCatalogTests : AnalyzerDiagnosticsTests
    {
        [Fact]
        public void Should_Discover_Roslynator_Fix_Providers_From_The_Bundled_Catalog()
        {
            // Arrange — production wiring: factory scans the bundled assemblies too.
            var factory = new CodeFixProviderFactory(
                A.Fake<ILogger<CodeFixProviderFactory>>(), CreateCatalog());

            // Act
            var fixableIds = factory.GetFixableDiagnosticIds().ToHashSet();

            // Assert — the Roslynator fixers that Assembly.Load("Roslynator.CodeFixes")
            // could never reach (analyzer-asset-only package, no lib/) are now registered.
            fixableIds.ShouldContain("RCS1104");
            fixableIds.ShouldContain("RCS1036");
            fixableIds.ShouldContain("RCS1213");
            factory.GetProviderForDiagnostic("RCS1104").ShouldNotBeNull();
        }

        [Fact]
        public void Should_Keep_BuiltIn_Fixers_For_Compiler_Diagnostics()
        {
            // Arrange — registration is first-wins; built-ins are scanned before Roslynator.
            var factory = new CodeFixProviderFactory(
                A.Fake<ILogger<CodeFixProviderFactory>>(), CreateCatalog());

            // Act & Assert
            factory.GetProviderForDiagnostic("CS0168").ShouldNotBeNull();
            factory.GetProviderForDiagnostic("CS0219").ShouldNotBeNull();
        }
    }

    public class DiagnosticComputationServiceTests : AnalyzerDiagnosticsTests
    {
        [Fact]
        public async Task Should_Combine_Compiler_And_Analyzer_Diagnostics()
        {
            // Arrange — real bundled Roslynator analyzers over a real in-memory compilation
            // with both a compiler finding (CS0219) and a Roslynator finding (RCS1104).
            var (_, project) = AdhocProjectBuilder.Create("AnalyzerProject",
                [("Conditional.cs", """
                  public class Conditional
                  {
                      public bool IsNull(object value)
                      {
                          int unused = 1;
                          return value == null ? true : false;
                      }
                  }
                  """)]);
            var compilation = (await project.GetCompilationAsync())!;
            var service = CreateComputationService(CreateCatalog());

            // Act
            var diagnostics = (await service.GetDiagnosticsAsync(project, compilation)).Diagnostics;

            // Assert
            var ids = diagnostics.Select(d => d.Id).ToHashSet();
            ids.ShouldContain("CS0219", customMessage: "compiler diagnostics must still be present");
            ids.ShouldContain("RCS1104", customMessage: "analyzer diagnostics must now surface");
        }

        [Fact]
        public async Task Should_Return_Compiler_Only_When_RunAnalyzers_Is_Disabled()
        {
            // Arrange
            var (_, project) = AdhocProjectBuilder.Create("CompilerOnlyProject",
                [("Conditional.cs", SourceWithRcs1104)]);
            var compilation = (await project.GetCompilationAsync())!;
            var service = CreateComputationService(CreateCatalog(), runAnalyzers: false);

            // Act
            var diagnostics = (await service.GetDiagnosticsAsync(project, compilation)).Diagnostics;

            // Assert — RoselineMCP:RunAnalyzers=false restores the old, compiler-only behavior.
            diagnostics.ShouldAllBe(d => !d.Id.StartsWith("RCS"));
        }

        [Fact]
        public async Task CompilerOnly_Fallback_Should_Never_Run_Analyzers()
        {
            // Arrange
            var (_, project) = AdhocProjectBuilder.Create("FallbackProject",
                [("Conditional.cs", SourceWithRcs1104)]);
            var compilation = (await project.GetCompilationAsync())!;

            // Act
            var diagnostics = (await DiagnosticComputationService.CompilerOnly
                .GetDiagnosticsAsync(project, compilation)).Diagnostics;

            // Assert
            diagnostics.ShouldAllBe(d => !d.Id.StartsWith("RCS"));
        }

        [Fact]
        public async Task Should_Run_The_Projects_Own_AnalyzerReferences()
        {
            // Arrange — an empty bundled catalog, but the project itself references an analyzer
            // (as MSBuildWorkspace would surface a target repository's own analyzers).
            var emptyCatalog = A.Fake<IAnalyzerCatalog>();
            A.CallTo(() => emptyCatalog.Analyzers).Returns(ImmutableArray<DiagnosticAnalyzer>.Empty);

            var (_, project) = AdhocProjectBuilder.Create("OwnAnalyzersProject",
                [("Widget.cs", "public class Widget { }")]);
            project = project.AddAnalyzerReference(
                new AnalyzerImageReference([new ReportOnEveryClassAnalyzer()]));
            var compilation = (await project.GetCompilationAsync())!;

            var service = CreateComputationService(emptyCatalog);

            // Act
            var diagnostics = (await service.GetDiagnosticsAsync(project, compilation)).Diagnostics;

            // Assert
            diagnostics.ShouldContain(d => d.Id == ReportOnEveryClassAnalyzer.Id);
        }

        [Fact]
        public async Task Should_Dedupe_Analyzers_Present_In_Both_Catalog_And_Project()
        {
            // Arrange — the same analyzer type both bundled and referenced by the project
            // (a target repository that itself uses Roslynator): its diagnostic must be
            // reported once, not twice.
            var catalog = A.Fake<IAnalyzerCatalog>();
            A.CallTo(() => catalog.Analyzers).Returns(
                ImmutableArray.Create<DiagnosticAnalyzer>(new ReportOnEveryClassAnalyzer()));

            var (_, project) = AdhocProjectBuilder.Create("DedupeProject",
                [("Widget.cs", "public class Widget { }")]);
            project = project.AddAnalyzerReference(
                new AnalyzerImageReference([new ReportOnEveryClassAnalyzer()]));
            var compilation = (await project.GetCompilationAsync())!;

            var service = CreateComputationService(catalog);

            // Act
            var diagnostics = (await service.GetDiagnosticsAsync(project, compilation)).Diagnostics;

            // Assert
            diagnostics.Count(d => d.Id == ReportOnEveryClassAnalyzer.Id).ShouldBe(1);
        }

        [Fact]
        public async Task Should_Survive_A_Throwing_Analyzer_And_Keep_Other_Diagnostics()
        {
            // Arrange — one broken analyzer must never fail the tool call, nor take the
            // healthy analyzers down with it.
            var catalog = A.Fake<IAnalyzerCatalog>();
            A.CallTo(() => catalog.Analyzers).Returns(
                ImmutableArray.Create<DiagnosticAnalyzer>(
                    new ThrowingAnalyzer(),
                    new ReportOnEveryClassAnalyzer()));

            var (_, project) = AdhocProjectBuilder.Create("ThrowingProject",
                [("Widget.cs", "public class Widget { }")]);
            var compilation = (await project.GetCompilationAsync())!;

            var service = CreateComputationService(catalog);

            // Act — must not throw
            var diagnostics = (await service.GetDiagnosticsAsync(project, compilation)).Diagnostics;

            // Assert — the healthy analyzer's diagnostic is still there.
            diagnostics.ShouldContain(d => d.Id == ReportOnEveryClassAnalyzer.Id);
        }
    }

    /// <summary>Reports a warning on every class declaration — a stand-in for a target
    /// project's own referenced analyzer.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class ReportOnEveryClassAnalyzer : DiagnosticAnalyzer
    {
        public const string Id = "TEST9001";

        private static readonly DiagnosticDescriptor Descriptor = new(
            Id, "Class found", "Class '{0}' found", "Testing",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Descriptor];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(
                c => c.ReportDiagnostic(Diagnostic.Create(
                    Descriptor, c.Node.GetLocation(),
                    ((ClassDeclarationSyntax)c.Node).Identifier.Text)),
                SyntaxKind.ClassDeclaration);
        }
    }

    /// <summary>Throws on every class declaration — a stand-in for a broken analyzer.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class ThrowingAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Descriptor = new(
            "TEST9002", "Never reported", "Never reported", "Testing",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Descriptor];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(
                _ => throw new InvalidOperationException("This analyzer is intentionally broken"),
                SyntaxKind.ClassDeclaration);
        }
    }
}
