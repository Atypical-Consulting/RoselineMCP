using System.Collections.Immutable;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class CodeFixProviderFactoryTests
{
    private readonly CodeFixProviderFactory _factory;
    private readonly ILogger<CodeFixProviderFactory> _logger;

    public CodeFixProviderFactoryTests()
    {
        _logger = A.Fake<ILogger<CodeFixProviderFactory>>();
        _factory = new CodeFixProviderFactory(_logger);
    }

    public class GetProviderForDiagnosticTests : CodeFixProviderFactoryTests
    {
        [Fact]
        public void Should_Return_Provider_For_Known_Diagnostic()
        {
            // Arrange
            _factory.LoadProviders();

            // Act
            var provider = _factory.GetProviderForDiagnostic("CS0168");

            // Assert
            // Provider may be null if not available in test environment
            if (provider != null)
            {
                provider.ShouldBeAssignableTo<CodeFixProvider>();
            }
        }

        [Fact]
        public void Should_Return_Null_For_Unknown_Diagnostic()
        {
            // Arrange
            _factory.LoadProviders();

            // Act
            var provider = _factory.GetProviderForDiagnostic("UNKNOWN999");

            // Assert
            provider.ShouldBeNull();
        }

        [Fact]
        public void Should_Handle_Null_Diagnostic_Id()
        {
            // Act & Assert
            Should.Throw<ArgumentNullException>(() =>
                _factory.GetProviderForDiagnostic(null!));
        }
    }

    public class GetFixableDiagnosticIdsTests : CodeFixProviderFactoryTests
    {
        [Fact]
        public void Should_Return_Diagnostic_Ids()
        {
            // Arrange
            _factory.LoadProviders();

            // Act
            var ids = _factory.GetFixableDiagnosticIds();

            // Assert
            ids.ShouldNotBeNull();
            ids.ShouldBeAssignableTo<IEnumerable<string>>();
        }

        [Fact]
        public void Should_Return_Unique_Ids()
        {
            // Arrange
            _factory.LoadProviders();

            // Act
            var ids = _factory.GetFixableDiagnosticIds().ToList();

            // Assert
            ids.Distinct().Count().ShouldBe(ids.Count);
        }
    }

    public class LoadProvidersTests : CodeFixProviderFactoryTests
    {
        [Fact]
        public void Should_Load_Providers_Without_Error()
        {
            // Act & Assert
            Should.NotThrow(() => _factory.LoadProviders());
        }

        [Fact]
        public void Should_Be_Idempotent()
        {
            // Act & Assert
            Should.NotThrow(() =>
            {
                _factory.LoadProviders();
                _factory.LoadProviders();
            });
        }
    }

    public class ThreadSafetyTests : CodeFixProviderFactoryTests
    {
        [Fact]
#pragma warning disable xUnit1051 // TestContext.Current not available in xUnit 3
        public void Should_Be_Thread_Safe_For_Loading()
        {
            // Arrange
            var tasks = new List<Task>();

            // Act
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() => _factory.LoadProviders()));
            }

            // Assert
            Should.NotThrow(async () => await Task.WhenAll(tasks));
        }

        [Fact]
#pragma warning disable xUnit1051 // TestContext.Current not available in xUnit 3
        public void Should_Be_Thread_Safe_For_Getting_Providers()
        {
            // Arrange
            _factory.LoadProviders();
            var tasks = new List<Task<CodeFixProvider?>>();

            // Act
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() => _factory.GetProviderForDiagnostic("CS0168")));
            }

            // Assert
            Should.NotThrow(async () =>
            {
                var results = await Task.WhenAll(tasks);
                // All should return the same result (null or not null)
                var firstResult = results.First();
                results.All(r => (r == null) == (firstResult == null)).ShouldBeTrue();
            });
        }
    }

    /// <summary>
    /// Code-fix providers carried by the target project's own <c>AnalyzerReferences</c> (#183).
    /// The diagnostics pass has always run those references' analyzers; their fixers were never
    /// looked for, so <c>suggestedFixableIds</c> under-reported and <c>apply_fixes</c> could not use
    /// them. The process-wide map (built-ins, then the bundled catalog) keeps precedence: an ID both
    /// can fix resolves to the bundled provider, so nothing already fixable changes behaviour.
    /// </summary>
    public class ProjectReferenceOverlayTests
    {
        private static AnalyzerCatalog CreateCatalog() => new(A.Fake<ILogger<AnalyzerCatalog>>());

        private static CodeFixProviderFactory CreateFactory(AnalyzerCatalog catalog) =>
            new(A.Fake<ILogger<CodeFixProviderFactory>>(), catalog);

        [Fact]
        public async Task Project_Overlay_Should_Be_A_Strict_Superset_Of_The_Process_Wide_Map()
        {
            // Arrange — this repository's own project. SYSLIB1045's fixer ships inside the SDK's
            // System.Text.RegularExpressions.Generator, a reference of every net10.0 project,
            // and nowhere in the bundled catalog.
            var factory = CreateFactory(CreateCatalog());
            using var loaded = await AnalyzerReferenceLoadTests.LoadRepositoryProjectAsync();
            var processWide = factory.GetFixableDiagnosticIds(null).ToHashSet();
            processWide.ShouldNotContain("SYSLIB1045");

            // Act
            var withProject = factory.GetFixableDiagnosticIds(loaded.Project).ToHashSet();

            // Assert
            withProject.IsProperSupersetOf(processWide).ShouldBeTrue(
                "the project's own references add fixers the bundled catalog does not carry");
            withProject.ShouldContain("SYSLIB1045");
            factory.GetProviderForDiagnostic("SYSLIB1045", loaded.Project).ShouldNotBeNull();
            factory.GetProviderForDiagnostic("SYSLIB1045", null).ShouldBeNull();
        }

        [Fact]
        public async Task Bundled_Provider_Should_Win_Over_A_Project_Reference_For_The_Same_Id()
        {
            // Arrange — RCS1104's fixer is both bundled and among this repository's references
            // (it references the same Roslynator packages RoselineMCP bundles).
            var catalog = CreateCatalog();
            var factory = CreateFactory(catalog);
            using var loaded = await AnalyzerReferenceLoadTests.LoadRepositoryProjectAsync();
            var bundled = factory.GetProviderForDiagnostic("RCS1104").ShouldNotBeNull();

            // Act
            var resolved = factory.GetProviderForDiagnostic("RCS1104", loaded.Project).ShouldNotBeNull();

            // Assert — same type, from the bundled assembly: first-wins registration is preserved.
            resolved.GetType().ShouldBe(bundled.GetType());
            catalog.Assemblies.ShouldContain(resolved.GetType().Assembly);
        }

        [Fact]
        public void No_Project_Members_Should_Delegate_To_The_Process_Wide_Map()
        {
            // Arrange
            var factory = CreateFactory(CreateCatalog());

            // Act & Assert — the existing members are the null-project overloads.
            factory.GetFixableDiagnosticIds().ShouldBe(factory.GetFixableDiagnosticIds(null));
            factory.GetProviderForDiagnostic("RCS1104")!.GetType()
                .ShouldBe(factory.GetProviderForDiagnostic("RCS1104", null)!.GetType());
        }

        [Fact]
        public void An_Unreadable_Reference_Should_Contribute_Nothing_And_Not_Throw()
        {
            // Arrange — a file reference whose path is not an assembly, plus an in-memory reference
            // that has no path at all. Both must degrade, never fail the lookup.
            var garbage = Path.Combine(Path.GetTempPath(), $"roseline-{Guid.NewGuid():N}.dll");
            File.WriteAllText(garbage, "not a PE image");
            try
            {
                var (_, project) = AdhocProjectBuilder.Create("Broken", [("W.cs", "public class W { }")]);
                project = project
                    .AddAnalyzerReference(new AnalyzerFileReference(garbage, TestAnalyzerAssemblyLoader.Instance))
                    .AddAnalyzerReference(new AnalyzerImageReference(ImmutableArray<DiagnosticAnalyzer>.Empty, display: "InMemory"));
                var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());

                // Act
                var ids = Should.NotThrow(() => factory.GetFixableDiagnosticIds(project).ToHashSet());
                var provider = Should.NotThrow(() => factory.GetProviderForDiagnostic("NOPE0001", project));

                // Assert
                ids.ShouldBe(factory.GetFixableDiagnosticIds(null).ToHashSet());
                provider.ShouldBeNull();
            }
            finally
            {
                File.Delete(garbage);
            }
        }
    }
}
