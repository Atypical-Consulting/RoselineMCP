using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    /// <summary>
    /// A class whose last member is a permanent CS0103, preceded by <paramref name="filler"/> filler
    /// methods so the broken line sits deep in the file and its line number is sensitive to edits
    /// above it.
    /// </summary>
    private static string ShiftyClass(int filler)
    {
        var lines = new List<string> { "public class Shifty", "{" };
        lines.AddRange(Enumerable.Range(0, filler).Select(i => $"    public int F{i}() => {i};"));
        lines.Add("    public int Broken() => Missing.Thing();");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    [Fact]
    public async Task A_Preexisting_Error_Pushed_Down_By_An_Edit_Above_It_Is_Neither_Introduced_Nor_Resolved()
    {
        // Arrange — the pre-existing CS0103 sits around line 80; the candidate adds three methods
        // above it, so it moves to line 83 without changing in any other way.
        var (workspace, project) = AdhocProjectBuilder.Create("Shift", [("Shifty.cs", ShiftyClass(77))]);
        using var _ = workspace;
        var baseline = project.Solution;
        var document = baseline.Projects.Single().Documents.Single();
        var candidate = baseline.WithDocumentText(document.Id, SourceText.From(ShiftyClass(80)));

        // Guard the premise: the error really did move.
        var before = await CreateService().VerifyAsync(null, baseline, cancellationToken: TestContext.Current.CancellationToken);
        var after = await CreateService().VerifyAsync(null, candidate, cancellationToken: TestContext.Current.CancellationToken);
        before.Errors!.Single().Line.ShouldBe(80);
        after.Errors!.Single().Line.ShouldBe(83);

        // Act
        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        // Assert — a set difference over DiagnosticDetail (which carries line/column) would call this
        // one error both introduced and resolved, and refuse a write for a break the edit never made.
        verdict.Introduced.ShouldBeNull();
        verdict.Resolved.ShouldBeNull();
        verdict.Preexisting.ShouldBe(1);
        verdict.Compiles.ShouldBe(false);
    }

    [Fact]
    public async Task A_Second_Identical_Error_In_The_Same_File_Counts_Once_As_Introduced()
    {
        // Arrange — the baseline already has one CS0103 for 'Missing'; the candidate adds a second
        // occurrence with a byte-identical message. Set semantics would see "the key is already
        // there" and report nothing introduced.
        var (workspace, project) = AdhocProjectBuilder.Create("Dup",
            [("Dup.cs", "public class Dup { public int A() => Missing.Thing(); }")]);
        using var _ = workspace;
        var baseline = project.Solution;
        var document = baseline.Projects.Single().Documents.Single();
        var candidate = baseline.WithDocumentText(document.Id, SourceText.From(
            "public class Dup { public int A() => Missing.Thing(); public int B() => Missing.Thing(); }"));

        // Act
        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        verdict.Introduced.ShouldNotBeNull();
        verdict.Introduced.Count.ShouldBe(1);
        verdict.Introduced[0].Id.ShouldBe("CS0103");
        verdict.Resolved.ShouldBeNull();
        verdict.Preexisting.ShouldBe(1);
    }

    [Fact]
    public async Task An_Edit_That_Fixes_An_Error_Reports_It_As_Resolved()
    {
        var (workspace, project) = AdhocProjectBuilder.Create("Fixed",
            [("Fixed.cs", "public class Fixed { public int A() => Missing.Thing(); }")]);
        using var _ = workspace;
        var baseline = project.Solution;
        var document = baseline.Projects.Single().Documents.Single();
        var candidate = baseline.WithDocumentText(document.Id, SourceText.From(
            "public class Fixed { public int A() => 1; }"));

        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        verdict.Resolved.ShouldNotBeNull();
        verdict.Resolved.Count.ShouldBe(1);
        verdict.Resolved[0].Id.ShouldBe("CS0103");
        verdict.Introduced.ShouldBeNull();
        verdict.Preexisting.ShouldBe(0);
        verdict.Compiles.ShouldBe(true);
    }

    [Fact]
    public async Task A_Clean_Edit_On_A_Broken_Project_Introduces_Nothing()
    {
        // The gate is `introduced`, never `compiles`: an agent sent to fix a broken branch must
        // still be able to write.
        var (workspace, project) = AdhocProjectBuilder.Create("Broken",
            [
                ("Broken.cs", "public class Broken { public int A() => Missing.Thing(); }"),
                ("Fine.cs", "public class Fine { public int B() => 1; }")
            ]);
        using var _ = workspace;
        var baseline = project.Solution;
        var fine = baseline.Projects.Single().Documents.Single(d => d.Name == "Fine.cs");
        var candidate = baseline.WithDocumentText(fine.Id, SourceText.From(
            "public class Fine { public int B() => 2; }"));

        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        verdict.Compiles.ShouldBe(false);
        verdict.Introduced.ShouldBeNull();
        verdict.Preexisting.ShouldBe(1);
    }

    [Fact]
    public async Task Delta_Mode_Does_Not_Emit_The_Absolute_Error_List()
    {
        // `errors` exists so check_compilation is not left with an empty payload; repeating every
        // pre-existing error on every edit would be exactly the token bloat this server avoids.
        var (workspace, project) = AdhocProjectBuilder.Create("Quiet",
            [("Quiet.cs", "public class Quiet { public int A() => Missing.Thing(); }")]);
        using var _ = workspace;
        var baseline = project.Solution;
        var document = baseline.Projects.Single().Documents.Single();
        var candidate = baseline.WithDocumentText(document.Id, SourceText.From(
            "public class Quiet { public int A() => Missing.Thing(); public int B() => 2; }"));

        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        verdict.Errors.ShouldBeNull();
        verdict.Compiles.ShouldBe(false);
        verdict.Preexisting.ShouldBe(1);
    }

    [Fact]
    public async Task Introduced_Is_Truncated_To_Max_And_Counts_The_Rest()
    {
        var body = string.Join(" ", Enumerable.Range(0, 5).Select(i => $"public int M{i}() => Missing{i}.Thing();"));
        var (workspace, project) = AdhocProjectBuilder.Create("Flood",
            [("Flood.cs", "public class Flood { }")]);
        using var _ = workspace;
        var baseline = project.Solution;
        var document = baseline.Projects.Single().Documents.Single();
        var candidate = baseline.WithDocumentText(document.Id, SourceText.From($"public class Flood {{ {body} }}"));

        var verdict = await CreateService().VerifyAsync(baseline, candidate, max: 2, cancellationToken: TestContext.Current.CancellationToken);

        verdict.Introduced.ShouldNotBeNull();
        verdict.Introduced.Count.ShouldBe(2);
        verdict.Omitted.ShouldBe(3);
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

    [Fact]
    public async Task A_Bare_Project_With_No_Containing_Solution_Reports_An_Incomplete_Scope()
    {
        // Arrange — one project, loaded on its own (no .sln). Changing a public signature is safe
        // *within* this project and breaks anything outside it, which is exactly what the workspace
        // cannot see.
        var (workspace, project) = AdhocProjectBuilder.Create("Lonely",
            [("Api.cs", "public class Api { public int Value(int a) => a; }")]);
        using var _ = workspace;
        var baseline = project.Solution;
        var document = baseline.Projects.Single().Documents.Single();
        var candidate = baseline.WithDocumentText(document.Id, SourceText.From(
            "public class Api { public int Value(int a, int b) => a + b; }"));

        // Act
        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        // Assert — the write still proceeds (nothing was introduced *that we can see*), but the
        // caller is told the gate was partial rather than handed a false green.
        verdict.Introduced.ShouldBeNull();
        verdict.ScopeComplete.ShouldBeFalse();
        verdict.Notes.ShouldNotBeNull();
        verdict.Notes.ShouldContain(n => n.Contains("solution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_Project_Loaded_Through_Its_Solution_Reports_A_Complete_Scope()
    {
        var (workspace, anchor) = CreateChain();
        using var _ = workspace;
        var baseline = anchor.Solution;
        var candidate = WithChangedDocument(baseline, "Core", "Thing.cs",
            "public class Thing { public int Value() => 2; }");

        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        verdict.ScopeComplete.ShouldBeTrue();
        verdict.Notes.ShouldBeNull();
    }

    [Fact]
    public async Task Absolute_Mode_Reports_The_Incomplete_Scope_Too()
    {
        // check_compilation against a bare .csproj answers "this project compiles", which is not
        // the same claim as "the build is green" — and the difference has to be on the wire.
        var (workspace, project) = AdhocProjectBuilder.Create("Lonely",
            [("Api.cs", "public class Api { public int Value(int a) => a; }")]);
        using var _ = workspace;

        var verdict = await CreateService().VerifyAsync(null, project.Solution, cancellationToken: TestContext.Current.CancellationToken);

        verdict.Compiles.ShouldBe(true);
        verdict.ScopeComplete.ShouldBeFalse();
        verdict.Notes.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_File_Shared_By_A_Project_Outside_The_Scope_Reports_An_Incomplete_Scope()
    {
        // A multi-targeted project loads as one Roslyn project per TFM over the same file paths, and
        // a linked <Compile Include="../Shared.cs" /> does the same across projects. The candidate
        // changes ONE DocumentId; the write changes the file for every project that includes it.
        // Without this check the sibling keeps its pre-edit text, never enters the scope, and an edit
        // that breaks it comes back introduced:[] / compiles:true / scopeComplete:true — a false
        // green presented as full coverage, which is the one thing this field exists to prevent.
        const string shared = "public class Shared { public int Value() => 1; }";
        var baseDirectory = Path.Combine(Path.GetTempPath(), "roseline-tests", Guid.NewGuid().ToString("n"));
        var sharedPath = Path.Combine(baseDirectory, "Shared.cs");

        var workspace = new AdhocWorkspace();
        var solution = workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(),
            VersionStamp.Create(),
            filePath: Path.Combine(baseDirectory, "Multi.sln")));

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Where(path => path.Length > 0)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();

        var ids = new List<ProjectId>();
        foreach (var tfm in new[] { "net8.0", "net10.0" })
        {
            var projectId = ProjectId.CreateNewId();
            ids.Add(projectId);
            solution = solution.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), $"Multi({tfm})", "Multi",
                LanguageNames.CSharp,
                filePath: Path.Combine(baseDirectory, "Multi.csproj"),
                metadataReferences: references,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), "Shared.cs", SourceText.From(shared), filePath: sharedPath);
        }

        using var _ = workspace;
        var baseline = solution;

        // Edit only the first leg's document — exactly what CodeEditService does.
        var firstLegDocument = baseline.GetProject(ids[0])!.Documents.Single();
        var candidate = baseline.WithDocumentText(
            firstLegDocument.Id, SourceText.From("public class Shared { public int Value() => 2; }"));

        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        verdict.Scope.ShouldBe(["Multi(net8.0)"]);
        verdict.ScopeComplete.ShouldBeFalse();
        verdict.Notes.ShouldNotBeNull();
        verdict.Notes.ShouldContain(n => n.Contains("Multi(net10.0)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Change_Whose_Files_Are_Not_Shared_Keeps_A_Complete_Scope()
    {
        // The guard must not fire on the ordinary case, or scopeComplete becomes noise nobody reads.
        var (workspace, anchor) = CreateChain();
        using var _ = workspace;
        var baseline = anchor.Solution;
        var candidate = WithChangedDocument(baseline, "Core", "Thing.cs",
            "public class Thing { public int Value() => 3; }");

        var verdict = await CreateService().VerifyAsync(baseline, candidate, cancellationToken: TestContext.Current.CancellationToken);

        verdict.ScopeComplete.ShouldBeTrue();
        verdict.Notes.ShouldBeNull();
    }

    [Fact]
    public async Task Max_Of_Zero_Clamps_Instead_Of_Returning_Everything()
    {
        // `max: 0` reads as "just the verdict"; treating it as unbounded would hand back exactly the
        // blow-up the cap exists to prevent.
        var body = string.Join(" ", Enumerable.Range(0, 6).Select(i => $"public int M{i}() => Missing{i}.Thing();"));
        var (workspace, project) = AdhocProjectBuilder.Create("Many", [("Many.cs", $"public class Many {{ {body} }}")]);
        using var _ = workspace;

        var verdict = await CreateService().VerifyAsync(null, project.Solution, max: 0, cancellationToken: TestContext.Current.CancellationToken);

        verdict.Errors!.Count.ShouldBe(1);
        verdict.Omitted.ShouldBe(5);
    }
}
