using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="VerificationService"/> against real Roslyn compilations built from in-memory
/// <see cref="AdhocWorkspace"/> solutions — the compiler is the only honest judge of "does this
/// compile", so nothing here is faked except the logger.
/// </summary>
public class VerificationServiceTests
{
    private static IVerificationService CreateService() =>
        new VerificationService(A.Fake<ILogger<VerificationService>>(), DiagnosticComputationService.CompilerOnly);

    /// <summary>Core ← Mid ← Web: a three-project chain where only Web is a leaf.</summary>
    private static (AdhocWorkspace Workspace, Project Anchor) CreateChain(string coreBody = "public int Value() => 1;")
    {
        return AdhocProjectBuilder.CreateSolution(
            [
                ("Core", [("Thing.cs", $"public class Thing {{ {coreBody} }}")]),
                ("Mid", [("Middle.cs", "public class Middle { public int Use() => new Thing().Value(); }")]),
                ("Web", [("Endpoint.cs", "public class Endpoint { public int Call() => new Middle().Use(); }")])
            ],
            projectReferences: [("Mid", "Core"), ("Web", "Mid")],
            solutionFileName: "Chain.sln");
    }

    private static Solution WithChangedDocument(Solution solution, string projectName, string fileName, string newText)
    {
        var project = solution.Projects.Single(p => p.Name == projectName);
        var document = project.Documents.Single(d => d.Name == fileName);
        return solution.WithDocumentText(document.Id, SourceText.From(newText));
    }

    [Fact]
    public async Task Scope_Covers_The_Changed_Project_And_Its_Transitive_Dependents()
    {
        // Arrange — edit Core, which Mid references and Web references transitively.
        var (workspace, anchor) = CreateChain();
        using var _ = workspace;
        var baseline = anchor.Solution;
        var candidate = WithChangedDocument(baseline, "Core", "Thing.cs",
            "public class Thing { public int Value() => 2; }");

        // Act
        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        // Assert — a file-only or project-only scope would miss exactly the cross-project breakage
        // this gate exists to catch.
        verdict.Scope.ShouldNotBeNull();
        verdict.Scope.ShouldBe(["Core", "Mid", "Web"], ignoreOrder: true);
    }

    [Fact]
    public async Task Scope_Excludes_Projects_That_Cannot_Be_Affected()
    {
        // Arrange — edit Web, the leaf. Nothing depends on it.
        var (workspace, anchor) = CreateChain();
        using var _ = workspace;
        var baseline = anchor.Solution;
        var candidate = WithChangedDocument(baseline, "Web", "Endpoint.cs",
            "public class Endpoint { public int Call() => new Middle().Use() + 1; }");

        // Act
        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        // Assert — paying to compile Core and Mid here would be pure waste.
        verdict.Scope.ShouldBe(["Web"]);
    }

    [Fact]
    public async Task Absolute_Mode_Populates_Errors_And_Sets_Compiles_False()
    {
        // Arrange — a project that does not compile, verified with no baseline.
        var (workspace, project) = AdhocProjectBuilder.Create("Broken",
            [("Broken.cs", "public class Broken { public int Nope() => Missing.Thing(); }")]);
        using var _ = workspace;

        // Act
        var verdict = await CreateService().VerifyAsync(null, project.Solution, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        verdict.Compiles.ShouldBe(false);
        verdict.Errors.ShouldNotBeNull();
        verdict.Errors.ShouldNotBeEmpty();
        verdict.Errors.ShouldAllBe(e => e.Severity == "error");
        verdict.Errors[0].Project.ShouldBe("Broken");
        // introduced/resolved are meaningless without a before-state and must stay absent.
        verdict.Introduced.ShouldBeNull();
        verdict.Resolved.ShouldBeNull();
    }

    [Fact]
    public async Task Absolute_Mode_Reports_Compiles_True_For_A_Clean_Solution()
    {
        var (workspace, anchor) = CreateChain();
        using var _ = workspace;

        var verdict = await CreateService().VerifyAsync(null, anchor.Solution, cancellationToken: TestContext.Current.CancellationToken);

        verdict.Compiles.ShouldBe(true);
        verdict.Errors.ShouldBeNull();
        verdict.Scope.ShouldBe(["Core", "Mid", "Web"], ignoreOrder: true);
    }

    [Fact]
    public async Task Absolute_Mode_Truncates_To_Max_And_Counts_The_Rest()
    {
        // Arrange — six independent unresolved names, capped at two.
        var body = string.Join(" ", Enumerable.Range(0, 6).Select(i => $"public int M{i}() => Missing{i}.Thing();"));
        var (workspace, project) = AdhocProjectBuilder.Create("Many", [("Many.cs", $"public class Many {{ {body} }}")]);
        using var _ = workspace;

        // Act
        var verdict = await CreateService().VerifyAsync(null, project.Solution, max: 2, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        verdict.Errors.ShouldNotBeNull();
        verdict.Errors.Count.ShouldBe(2);
        verdict.Omitted.ShouldBe(4);
    }
}
