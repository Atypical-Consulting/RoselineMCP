using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Additional tests for DiagnosticFilterService covering FilterByFiles and IsFixableDiagnostic.
/// </summary>
public class DiagnosticFilterServiceAdditionalTests
{
    private readonly DiagnosticFilterService _service;

    public DiagnosticFilterServiceAdditionalTests()
    {
        // Mirror the production wiring: the factory scans the bundled analyzer catalog
        // (Roslynator fixers) in addition to the Roslyn built-ins.
        var catalog = new AnalyzerCatalog(A.Fake<ILogger<AnalyzerCatalog>>());
        _service = new DiagnosticFilterService(new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>(), catalog));
    }

    public class FilterByFilesTests : DiagnosticFilterServiceAdditionalTests
    {
        [Fact]
        public void Should_Return_True_When_No_Files()
        {
            // Arrange
            var diagnostic = CreateDiagnosticWithLocation("CS0168", "MyController.cs");

            // Act
            var result = _service.FilterByFiles(diagnostic, null);

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Return_True_When_Files_Empty()
        {
            // Arrange
            var diagnostic = CreateDiagnosticWithLocation("CS0168", "MyController.cs");

            // Act
            var result = _service.FilterByFiles(diagnostic, new List<string>());

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Return_False_When_No_Location()
        {
            // Arrange
            var diagnostic = CreateDiagnosticNoLocation("CS0168");
            var files = new List<string> { "Controller.cs" };

            // Act
            var result = _service.FilterByFiles(diagnostic, files);

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public void Should_Return_True_When_File_Pattern_Matches()
        {
            // Arrange
            var diagnostic = CreateDiagnosticWithLocation("CS0168", "HomeController.cs");
            var files = new List<string> { "Controller.cs" };

            // Act
            var result = _service.FilterByFiles(diagnostic, files);

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Return_False_When_No_Pattern_Matches()
        {
            // Arrange
            var diagnostic = CreateDiagnosticWithLocation("CS0168", "HomeService.cs");
            var files = new List<string> { "Controller.cs", "Repository.cs" };

            // Act
            var result = _service.FilterByFiles(diagnostic, files);

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public void Should_Be_Case_Insensitive_For_File_Match()
        {
            // Arrange
            var diagnostic = CreateDiagnosticWithLocation("CS0168", "HOMECONTROLLER.CS");
            var files = new List<string> { "homecontroller.cs" };

            // Act
            var result = _service.FilterByFiles(diagnostic, files);

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Match_Partial_File_Path()
        {
            // Arrange
            var diagnostic = CreateDiagnosticWithLocation("CS0168", "src/Controllers/HomeController.cs");
            var files = new List<string> { "HomeController" };

            // Act
            var result = _service.FilterByFiles(diagnostic, files);

            // Assert
            result.ShouldBeTrue();
        }

        private Diagnostic CreateDiagnosticWithLocation(string id, string filePath)
        {
            // Create a diagnostic with a specified file location using source text
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                "class Foo {}",
                path: filePath);
            var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(0, 5);
            var location = Location.Create(tree, span);

            var descriptor = new DiagnosticDescriptor(
                id, "Test", "Test message", "Test",
                DiagnosticSeverity.Warning, isEnabledByDefault: true);
            return Diagnostic.Create(descriptor, location);
        }

        private Diagnostic CreateDiagnosticNoLocation(string id)
        {
            var descriptor = new DiagnosticDescriptor(
                id, "Test", "Test message", "Test",
                DiagnosticSeverity.Warning, isEnabledByDefault: true);
            return Diagnostic.Create(descriptor, Location.None);
        }
    }

    /// <summary>
    /// IsFixableDiagnostic used to answer from a hand-maintained static list of ~50 hardcoded
    /// diagnostic IDs that was completely disconnected from what ICodeFixProviderFactory
    /// actually discovers at runtime. It is now a pure pass-through over the factory's real,
    /// dynamically-discovered set — and since RoselineMCP bundles the Roslynator analyzer/fixer
    /// assemblies into an <c>analyzers/</c> folder next to RoselineMCP.dll (the packages are
    /// analyzer-asset-only, so <c>Assembly.Load("Roslynator.CodeFixes")</c> alone could never
    /// find them), the discovered set genuinely includes Roslynator's RCS fixers. StyleCop is
    /// neither referenced nor bundled, so SA* IDs remain not fixable in this deployment.
    /// </summary>
    public class IsFixableDiagnosticTests : DiagnosticFilterServiceAdditionalTests
    {
        [Theory]
        [InlineData("CS0168")] // Microsoft.CodeAnalysis.CSharp.RemoveUnusedVariable
        [InlineData("CS0219")] // Microsoft.CodeAnalysis.CSharp.RemoveUnusedVariable
        [InlineData("IDE0001")] // Microsoft.CodeAnalysis.CSharp.SimplifyTypeNames
        [InlineData("IDE0004")] // Microsoft.CodeAnalysis.CSharp.RemoveUnnecessaryCast
        [InlineData("RCS1036")] // Roslynator RemoveUnnecessaryBlankLine (bundled analyzer catalog)
        [InlineData("RCS1104")] // Roslynator SimplifyConditionalExpression (bundled analyzer catalog)
        [InlineData("RCS1213")] // Roslynator RemoveUnusedMemberDeclaration (bundled analyzer catalog)
        public void Should_Return_True_For_Id_With_A_Real_Runtime_Provider(string id)
        {
            // Act
            var result = _service.IsFixableDiagnostic(id);

            // Assert
            result.ShouldBeTrue($"{id} should be fixable — a provider for it ships in this deployment");
        }

        [Theory]
        [InlineData("UNKNOWN001")]
        [InlineData("TEST123")]
        [InlineData("NOTREAL456")]
        [InlineData("CS9999")]
        public void Should_Return_False_For_Unknown_Id(string id)
        {
            // Act
            var result = _service.IsFixableDiagnostic(id);

            // Assert
            result.ShouldBeFalse($"{id} should not be fixable");
        }

        [Fact]
        public void Should_Return_False_For_Empty_Id()
        {
            // Act
            var result = _service.IsFixableDiagnostic("");

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public void Should_Return_True_For_Every_Id_The_Factory_Actually_Discovered()
        {
            // Arrange — ask the real, dynamically-loaded factory what it found
            var catalog = new AnalyzerCatalog(A.Fake<ILogger<AnalyzerCatalog>>());
            var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>(), catalog);
            var service = new DiagnosticFilterService(factory);
            var discoveredIds = factory.GetFixableDiagnosticIds().ToList();

            // Sanity: the factory did discover real providers from the referenced assemblies
            discoveredIds.ShouldNotBeEmpty();

            // Act & Assert — every ID the factory says it can fix, the filter service must
            // agree is fixable, because it is now driven by the same source of truth.
            foreach (var id in discoveredIds)
            {
                service.IsFixableDiagnostic(id).ShouldBeTrue(
                    $"{id} was discovered by CodeFixProviderFactory but IsFixableDiagnostic disagreed");
            }
        }

        [Fact]
        public void Should_Reject_Id_That_Has_No_Loaded_Provider()
        {
            // StyleCop.Analyzers is neither referenced nor bundled in this deployment, so even
            // though SA1101 sat on the old hand-maintained hardcoded list, no provider for it
            // is actually loadable — IsFixableDiagnostic must say "no". (RCS IDs, by contrast,
            // ARE fixable now that the Roslynator assemblies are bundled — see the
            // Should_Return_True_For_Id_With_A_Real_Runtime_Provider cases.)
            _service.IsFixableDiagnostic("SA1101").ShouldBeFalse();
        }

        [Fact]
        public void Should_Reject_Roslynator_Id_When_Factory_Has_No_Analyzer_Catalog()
        {
            // Without the bundled analyzer catalog the factory only sees the Roslyn built-ins:
            // the Roslynator packages are analyzer-asset-only (no lib/), so the name-based
            // Assembly.Load fallback can never find their fixers.
            var factoryWithoutCatalog = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
            var service = new DiagnosticFilterService(factoryWithoutCatalog);

            service.IsFixableDiagnostic("RCS1213").ShouldBeFalse();
        }

        [Fact]
        public void Should_Be_Driven_Purely_By_The_Injected_Factory_Not_A_Hardcoded_List()
        {
            // Arrange — a fake factory with a made-up, otherwise-meaningless ID
            var fakeFactory = A.Fake<ICodeFixProviderFactory>();
            A.CallTo(() => fakeFactory.GetFixableDiagnosticIds()).Returns(["TOTALLY_MADE_UP_ID"]);
            var service = new DiagnosticFilterService(fakeFactory);

            // Act & Assert — fixability tracks the factory, not any baked-in knowledge
            service.IsFixableDiagnostic("TOTALLY_MADE_UP_ID").ShouldBeTrue();
            service.IsFixableDiagnostic("CS0168").ShouldBeFalse(); // real ID, but absent from this factory
        }
    }

    public class ShouldIncludeDiagnosticSuppressedTests : DiagnosticFilterServiceAdditionalTests
    {
        [Fact]
        public void Should_Return_True_For_Warning_Greater_Than_Info_Filter()
        {
            // Arrange
            var diagnostic = CreateDiagnostic(DiagnosticSeverity.Warning);

            // Act
            var result = _service.ShouldIncludeDiagnostic(diagnostic, "Info");

            // Assert
            result.ShouldBeTrue(); // Warning >= Info
        }

        [Fact]
        public void Should_Return_False_For_Info_Less_Than_Error_Filter()
        {
            // Arrange
            var diagnostic = CreateDiagnostic(DiagnosticSeverity.Info);

            // Act
            var result = _service.ShouldIncludeDiagnostic(diagnostic, "Error");

            // Assert
            result.ShouldBeFalse(); // Info < Error
        }

        [Fact]
        public void Should_Return_True_For_Error_Greater_Than_Warning_Filter()
        {
            // Arrange
            var diagnostic = CreateDiagnostic(DiagnosticSeverity.Error);

            // Act
            var result = _service.ShouldIncludeDiagnostic(diagnostic, "Warning");

            // Assert
            result.ShouldBeTrue(); // Error >= Warning
        }

        private Diagnostic CreateDiagnostic(DiagnosticSeverity severity)
        {
            var descriptor = new DiagnosticDescriptor(
                "TEST001", "Test", "Test message", "Test",
                severity, isEnabledByDefault: true);
            return Diagnostic.Create(descriptor, Location.None);
        }
    }
}
