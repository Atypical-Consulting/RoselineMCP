using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// The safety rail under #242's load-time strip: <c>AnalyzerReferenceFilter</c> must remove only
/// the analyzer references Roslyn's own project-state checksum cannot serialize, and <b>never</b>
/// a real analyzer.
/// </summary>
/// <remarks>
/// <para>
/// The strip is an allow-list (<c>AnalyzerFileReference</c> / <c>AnalyzerImageReference</c>), and an
/// allow-list that is too narrow silently deletes working analyzers and source generators from every
/// loaded solution — a far worse failure than the one #242 reports. So the predicate is pinned
/// against a <b>real</b> MSBuild-loaded project rather than a hand-built fixture: this repository's
/// own <c>RoselineMCP.csproj</c>, loaded through the production <see cref="ProjectLoader"/>, exactly
/// as <see cref="AnalyzerReferenceLoadTests"/> already does one directory up.
/// </para>
/// <para>
/// Measured with MSBuildWorkspace 5.9.0: that project yields 8 analyzer references, all plain
/// <c>AnalyzerFileReference</c>, carrying 280 analyzers and 5 source generators between them. If a
/// future Roslyn emits a reference type the allow-list does not name, this test goes red here rather
/// than in production.
/// </para>
/// <para>
/// The filter is <c>internal</c> and this assembly exposes no <c>InternalsVisibleTo</c> (see
/// <c>RoselineToolDescriptions</c>' remarks), so it is reached by reflection — the established
/// pattern here, see <see cref="ProjectLoaderFileAnchorTests"/>.
/// </para>
/// </remarks>
public class AnalyzerReferenceFilterTests
{
    private static readonly Type FilterType = typeof(ProjectLoader).Assembly
        .GetType("RoselineMCP.Services.AnalyzerReferenceFilter", throwOnError: true)!;

    /// <summary>Invokes the internal static <c>AnalyzerReferenceFilter.IsSerializable</c>.</summary>
    internal static bool IsSerializable(AnalyzerReference reference) =>
        (bool)Invoke("IsSerializable", reference)!;

    /// <summary>Invokes the internal static <c>AnalyzerReferenceFilter.Strip</c>.</summary>
    internal static (Solution Solution, IReadOnlyList<string> Removed) Strip(Solution solution) =>
        ((Solution, IReadOnlyList<string>))Invoke("Strip", solution)!;

    private static object? Invoke(string name, object argument)
    {
        var method = FilterType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return method.Invoke(null, [argument]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public async Task Every_Analyzer_Reference_Of_A_Real_Project_Is_Serializable()
    {
        // Arrange — the production loader over this repository's own project.
        using var loaded = await AnalyzerReferenceLoadTests.LoadRepositoryProjectAsync();

        // Assert — the project really does carry analyzer references (a vacuous pass would make
        // every other assertion here meaningless), and every one of them survives the predicate.
        loaded.Project.AnalyzerReferences.Count.ShouldBeGreaterThan(0);
        foreach (var reference in loaded.Project.AnalyzerReferences)
        {
            IsSerializable(reference).ShouldBeTrue(
                $"{reference.Display} ({reference.GetType().FullName}) must not be stripped — it is a real analyzer reference");
        }
    }

    [Fact]
    public async Task Strip_Is_A_No_Op_When_Every_Reference_Is_Serializable()
    {
        // Arrange
        using var loaded = await AnalyzerReferenceLoadTests.LoadRepositoryProjectAsync();
        var before = loaded.Solution.Projects.Sum(p => p.AnalyzerReferences.Count);

        // Act
        var (stripped, removed) = Strip(loaded.Solution);

        // Assert — the clean case costs nothing and forks no solution: same instance, same counts.
        removed.ShouldBeEmpty();
        ReferenceEquals(stripped, loaded.Solution).ShouldBeTrue(
            "a solution with nothing to strip must be handed back untouched, not re-forked");
        stripped.Projects.Sum(p => p.AnalyzerReferences.Count).ShouldBe(before);
    }
}
