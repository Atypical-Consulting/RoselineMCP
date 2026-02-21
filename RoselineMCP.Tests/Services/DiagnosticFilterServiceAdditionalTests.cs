using Microsoft.CodeAnalysis;
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
        _service = new DiagnosticFilterService();
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

    public class IsFixableDiagnosticTests : DiagnosticFilterServiceAdditionalTests
    {
        [Theory]
        [InlineData("CS0168")]
        [InlineData("CS0219")]
        [InlineData("IDE0005")]
        [InlineData("IDE0001")]
        [InlineData("RCS1213")]
        [InlineData("SA1101")]
        public void Should_Return_True_For_Known_Fixable_Id(string id)
        {
            // Act
            var result = _service.IsFixableDiagnostic(id);

            // Assert
            result.ShouldBeTrue($"{id} should be fixable");
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
