using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Pins the measurement behind <c>suggest_fix_examples</c> (#180): the bundled catalog covers
/// almost every analyzer ID with a fixer, and still produces IDs no fixer can repair. Loose
/// bounds on purpose — the mechanism is pinned, not a Roslynator version.
/// </summary>
public class FixerCoverageTests
{
    [Fact]
    public void BundledCatalog_Reports_Its_Fixer_Coverage()
    {
        var catalog = new AnalyzerCatalog(A.Fake<ILogger<AnalyzerCatalog>>());
        var factory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>(), catalog);

        var fixable = factory.GetFixableDiagnosticIds().ToHashSet(StringComparer.Ordinal);

        fixable.Count.ShouldBeGreaterThan(500);
        catalog.Analyzers.Length.ShouldBeGreaterThan(100);

        var unfixable = catalog.Analyzers
            .SelectMany(a => a.SupportedDiagnostics)
            .Select(d => d.Id)
            .Where(id => !id.EndsWith("FadeOut", StringComparison.Ordinal))
            .Except(fixable)
            .ToList();

        unfixable.ShouldNotBeEmpty("IDs without a fixer existing is the premise of suggest_fix_examples");
    }
}
