using Microsoft.CodeAnalysis;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class DiagnosticFilterServiceTests
{
    private readonly DiagnosticFilterService _service;

    public DiagnosticFilterServiceTests()
    {
        _service = new DiagnosticFilterService();
    }

    public class ShouldAnalyzeProjectTests : DiagnosticFilterServiceTests
    {
        [Fact]
        public void Should_Return_True_When_No_Patterns()
        {
            // Act
            var result = _service.ShouldAnalyzeProject("TestProject", null, null);

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Include_When_Pattern_Matches()
        {
            // Act
            var result = _service.ShouldAnalyzeProject("Core.Test", "Core", null);

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Exclude_When_Pattern_Matches()
        {
            // Act
            var result = _service.ShouldAnalyzeProject("TestProject", null, "Test");

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public void Should_Prioritize_Exclude_Over_Include()
        {
            // Act
            var result = _service.ShouldAnalyzeProject("Core.Test", "Core", "Test");

            // Assert
            result.ShouldBeFalse();
        }

    }

    public class ShouldIncludeDiagnosticTests : DiagnosticFilterServiceTests
    {
        [Fact]
        public void Should_Include_All_When_No_Filter()
        {
            // Arrange
            var diagnostic = CreateDiagnostic(DiagnosticSeverity.Warning);

            // Act
            var result = _service.ShouldIncludeDiagnostic(diagnostic, null);

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Include_Matching_Severity()
        {
            // Arrange
            var diagnostic = CreateDiagnostic(DiagnosticSeverity.Error);

            // Act
            var result = _service.ShouldIncludeDiagnostic(diagnostic, "Error");

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Exclude_Non_Matching_Severity()
        {
            // Arrange
            var diagnostic = CreateDiagnostic(DiagnosticSeverity.Info);

            // Act
            var result = _service.ShouldIncludeDiagnostic(diagnostic, "Error");

            // Assert
            result.ShouldBeFalse();
        }

        [Fact]
        public void Should_Be_Case_Insensitive()
        {
            // Arrange
            var diagnostic = CreateDiagnostic(DiagnosticSeverity.Error);

            // Act
            var result = _service.ShouldIncludeDiagnostic(diagnostic, "error");

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Include_When_Invalid_Severity()
        {
            // Arrange
            var diagnostic = CreateDiagnostic(DiagnosticSeverity.Warning);

            // Act
            var result = _service.ShouldIncludeDiagnostic(diagnostic, "InvalidSeverity");

            // Assert
            result.ShouldBeTrue(); // Includes when invalid
        }

        private Diagnostic CreateDiagnostic(DiagnosticSeverity severity)
        {
            var descriptor = new DiagnosticDescriptor(
                "TEST001",
                "Test",
                "Test message",
                "Test",
                severity,
                isEnabledByDefault: true);

            return Diagnostic.Create(descriptor, Location.None);
        }
    }

    public class FilterByIdsTests : DiagnosticFilterServiceTests
    {
        [Fact]
        public void Should_Return_True_When_No_Ids()
        {
            // Arrange
            var diagnostic = CreateTestDiagnostic("CS0168");

            // Act
            var result = _service.FilterByIds(diagnostic, null);

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Return_True_When_Id_Matches()
        {
            // Arrange
            var diagnostic = CreateTestDiagnostic("CS0168");
            var ids = new List<string> { "CS0168", "CS0219" };

            // Act
            var result = _service.FilterByIds(diagnostic, ids);

            // Assert
            result.ShouldBeTrue();
        }

        [Fact]
        public void Should_Return_False_When_Id_Not_Matches()
        {
            // Arrange
            var diagnostic = CreateTestDiagnostic("CS0168");
            var ids = new List<string> { "CS0219", "IDE0005" };

            // Act
            var result = _service.FilterByIds(diagnostic, ids);

            // Assert
            result.ShouldBeFalse();
        }

        private Diagnostic CreateTestDiagnostic(string id)
        {
            var descriptor = new DiagnosticDescriptor(
                id,
                "Test",
                "Test message",
                "Test",
                DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            return Diagnostic.Create(descriptor, Location.None);
        }
    }

    public class GetSeverityPriorityTests : DiagnosticFilterServiceTests
    {
        [Fact]
        public void Should_Return_Higher_Priority_For_Errors()
        {
            // Act
            var errorPriority = _service.GetSeverityPriority("Error");
            var warningPriority = _service.GetSeverityPriority("Warning");

            // Assert
            errorPriority.ShouldBeGreaterThan(warningPriority);
        }

        [Fact]
        public void Should_Return_Lower_Priority_For_Info()
        {
            // Act
            var warningPriority = _service.GetSeverityPriority("Warning");
            var infoPriority = _service.GetSeverityPriority("Info");

            // Assert
            warningPriority.ShouldBeGreaterThan(infoPriority);
        }

        [Fact]
        public void Should_Return_Zero_For_Unknown_Severity()
        {
            // Act
            var priority = _service.GetSeverityPriority("Unknown");

            // Assert
            priority.ShouldBe(0);
        }
    }
}