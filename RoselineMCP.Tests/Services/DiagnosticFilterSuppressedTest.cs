using System.Reflection;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests targeting the suppressed diagnostic path in DiagnosticFilterService.
/// Roslyn's Diagnostic.WithIsSuppressed(bool) creates a suppressed diagnostic.
/// </summary>
public class DiagnosticFilterSuppressedTest
{
    private readonly DiagnosticFilterService _service =
        new(new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>()));

    [Fact]
    public void ShouldIncludeDiagnostic_Should_Return_False_For_Suppressed_Diagnostic()
    {
        // Arrange — create a diagnostic and mark it as suppressed
        // Roslyn provides Diagnostic.WithIsSuppressed(true) for this purpose
        var descriptor = new DiagnosticDescriptor(
            "CS0168", "Test", "Message", "Test",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        var originalDiag = Diagnostic.Create(descriptor, Location.None);
        // WithIsSuppressed is internal — use reflection
        var withIsSuppressed = typeof(Diagnostic)
            .GetMethod("WithIsSuppressed", BindingFlags.NonPublic | BindingFlags.Instance);
        withIsSuppressed.ShouldNotBeNull("Roslyn should have WithIsSuppressed");
        var suppressedDiag = (Diagnostic)withIsSuppressed!.Invoke(originalDiag, new object[] { true })!;

        // Assert precondition
        suppressedDiag.IsSuppressed.ShouldBeTrue();

        // Act
        var result = _service.ShouldIncludeDiagnostic(suppressedDiag, null);

        // Assert — suppressed diagnostic should NOT be included
        result.ShouldBeFalse();
    }

    [Fact]
    public void ShouldIncludeDiagnostic_Should_Return_True_For_Non_Suppressed_Error()
    {
        // Arrange
        var descriptor = new DiagnosticDescriptor(
            "CS0001", "Test", "Error message", "Test",
            DiagnosticSeverity.Error, isEnabledByDefault: true);
        var diagnostic = Diagnostic.Create(descriptor, Location.None);

        // Act — not suppressed
        var result = _service.ShouldIncludeDiagnostic(diagnostic, null);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void FilterByIds_Should_Exclude_Suppressed_Diagnostics_When_Used_With_ShouldInclude()
    {
        // Arrange — create a suppressed diagnostic
        var descriptor = new DiagnosticDescriptor(
            "IDE0005", "Test", "Message", "Test",
            DiagnosticSeverity.Warning, isEnabledByDefault: true);

        var original = Diagnostic.Create(descriptor, Location.None);
        var withIsSuppressed = typeof(Diagnostic)
            .GetMethod("WithIsSuppressed", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var suppressed = (Diagnostic)withIsSuppressed.Invoke(original, new object[] { true })!;

        // Act — FilterByIds only checks if ID is in the list, not suppression
        // The suppression check is in ShouldIncludeDiagnostic
        var passesFilter = _service.FilterByIds(suppressed, new List<string> { "IDE0005" });
        passesFilter.ShouldBeTrue(); // FilterByIds doesn't check suppression

        // But ShouldIncludeDiagnostic does check suppression
        var includedByShouldInclude = _service.ShouldIncludeDiagnostic(suppressed, null);
        includedByShouldInclude.ShouldBeFalse(); // Suppressed → excluded
    }
}
