using FakeItEasy;
using RoselineMCP.Interfaces;
using RoselineMCP.Tests.Services;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

/// <summary>
/// Tests for <c>check_compilation</c> — the sub-second answer to "does this still compile, and what
/// broke", meant to replace a 30–90 second <c>dotnet build</c> in an agent's edit loop. It answers
/// about on-disk state regardless of who edited it, so it serves agents that never call RoselineMCP's
/// write tools at all.
/// </summary>
public class CheckCompilationToolTests
{
    [Fact]
    public async Task Reports_Compiles_False_With_Errors_For_A_Broken_Project()
    {
        // Arrange
        var (workspace, project) = AdhocProjectBuilder.Create("Broken",
            [("Broken.cs", "public class Broken { public int Nope() => Missing.Thing(); }")]);
        using var _ = workspace;
        var loader = AdhocProjectBuilder.FakeLoaderFor(workspace, project);

        // Act
        var result = await CheckCompilationTool.CheckCompilation(
            loader, TestVerification.New(), cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        result.Ok.ShouldBeTrue();
        result.Data.ShouldNotBeNull();
        result.Data.Compiles.ShouldBe(false);
        result.Data.Errors.ShouldNotBeNull();
        result.Data.Errors.ShouldContain(e => e.Id == "CS0103");
    }

    [Fact]
    public async Task Reports_Compiles_True_For_A_Clean_Project()
    {
        var (workspace, project) = AdhocProjectBuilder.Create("Clean",
            [("Clean.cs", "public class Clean { public int Value() => 1; }")]);
        using var _ = workspace;

        var result = await CheckCompilationTool.CheckCompilation(
            AdhocProjectBuilder.FakeLoaderFor(workspace, project),
            TestVerification.New(),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();
        result.Data!.Compiles.ShouldBe(true);
        result.Data.Errors.ShouldBeNull();
    }

    [Fact]
    public async Task Reports_The_Resolved_Path_So_A_Caller_Can_Tell_Which_Checkout_Answered()
    {
        // The server's working directory is fixed at spawn and is not the agent's, so an omitted
        // `project` can silently resolve the main checkout while the agent works in a worktree.
        var baseDir = Path.Combine(Path.GetTempPath(), "roseline-tests", Guid.NewGuid().ToString("n"));
        var (workspace, project) = AdhocProjectBuilder.Create(
            "Acme", [("A.cs", "public class Widget { public int X => 1; }")], baseDir);
        using var _ = workspace;

        var result = await CheckCompilationTool.CheckCompilation(
            AdhocProjectBuilder.FakeLoaderFor(workspace, project),
            TestVerification.New(),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Data!.ResolvedPath.ShouldBe(Path.Combine(baseDir, "Acme.csproj"));
    }

    [Fact]
    public async Task Truncates_To_Max_And_Counts_What_It_Dropped()
    {
        var body = string.Join(" ", Enumerable.Range(0, 5).Select(i => $"public int M{i}() => Missing{i}.Thing();"));
        var (workspace, project) = AdhocProjectBuilder.Create("Many", [("Many.cs", $"public class Many {{ {body} }}")]);
        using var _ = workspace;

        var result = await CheckCompilationTool.CheckCompilation(
            AdhocProjectBuilder.FakeLoaderFor(workspace, project),
            TestVerification.New(),
            max: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        result.Data!.Errors!.Count.ShouldBe(2);
        result.Data.Omitted.ShouldBe(3);
    }

    [Fact]
    public async Task A_Load_Failure_Returns_The_Classified_Envelope_And_Never_Throws()
    {
        // The project-loading tools' documented contract: a failure is an ordinary tool call whose
        // payload says ok:false. Throwing to the MCP layer would break every client's error handling.
        var loader = A.Fake<IProjectLoader>();
        A.CallTo(() => loader.LoadAsync(A<string>._, A<CancellationToken>._))
            .Throws(new FileNotFoundException("Project not found: /nonexistent/x.csproj"));

        var result = await CheckCompilationTool.CheckCompilation(
            loader, TestVerification.New(), "/nonexistent/x.csproj",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Ok.ShouldBeFalse();
        result.Data.ShouldBeNull();
        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("NotFoundError");
        result.Error.CorrelationId.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// Regression for #199, the end-to-end shape of it: <c>errors[].file</c> must hang off the
    /// directory <c>resolvedPath</c> names, so joining the two lands on the real file. They diverged
    /// for a <c>.csproj</c> its ancestor <c>.sln</c> does not list — #151 made <c>resolvedPath</c>
    /// report the <c>.csproj</c> while the verification path still relativized against
    /// <c>Solution.FilePath</c>, the <c>.sln</c> Roslyn grafted the project onto. That produced
    /// <c>Scratch/Program.cs</c> under an anchor already ending in <c>Scratch</c>, i.e. a path to a
    /// file that does not exist — in the tool an agent leans on hardest in its edit loop.
    /// </summary>
    [Fact]
    public async Task Error_Paths_Hang_Off_ResolvedPath_When_The_Project_Is_Not_In_The_Sln()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "roseline-checkcomp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDirectory);

        try
        {
            var (workspace, scratch, scratchCsproj) = AdhocProjectBuilder.CreateUnlistedProjectSolutionOnDisk(
                baseDirectory,
                [("MainWidget.cs", "namespace MainNs { public class MainWidget { } }")],
                [("Program.cs", "public class Program { public int Nope() => Missing.Thing(); }")]);
            using var _ = workspace;
            var loader = AdhocProjectBuilder.FakeLoaderFor(workspace, scratch, scratchCsproj);

            var result = await CheckCompilationTool.CheckCompilation(
                loader, TestVerification.New(), scratchCsproj,
                cancellationToken: TestContext.Current.CancellationToken);

            result.Ok.ShouldBeTrue();
            result.Data!.ResolvedPath.ShouldBe(scratchCsproj);
            result.Data.Compiles.ShouldBe(false);
            result.Data.Errors.ShouldNotBeNull();

            var error = result.Data.Errors.First(e => e.Project == "Scratch");
            error.File.ShouldBe("Program.cs");

            var reconstructed = Path.Combine(Path.GetDirectoryName(result.Data.ResolvedPath)!, error.File);
            File.Exists(reconstructed).ShouldBeTrue(
                $"'{reconstructed}' should be the real file on disk, but resolvedPath and the "
                + "errors[].file anchor disagree");
        }
        finally
        {
            Directory.Delete(baseDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The other half of #199: in the common case — the project <em>is</em> in the solution, so the
    /// <c>.sln</c> answers — paths must stay solution-root-relative. The fix moves the anchor to the
    /// caller's <c>resolvedPath</c>; here the two coincide, and a change in this case would be a
    /// wire-shape regression rather than a fix.
    /// </summary>
    [Fact]
    public async Task Error_Paths_Stay_Solution_Root_Relative_When_The_Solution_Answers()
    {
        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
            [("Listed", [("Program.cs", "public class Program { public int Nope() => Missing.Thing(); }")])],
            solutionFileName: "Repo.sln");
        using var _ = workspace;

        // No explicit resolvedPath: the handle falls back to Solution.FilePath, the .sln — exactly
        // what the real loader reports for a project its solution lists.
        var result = await CheckCompilationTool.CheckCompilation(
            AdhocProjectBuilder.FakeLoaderFor(workspace, anchor),
            TestVerification.New(),
            cancellationToken: TestContext.Current.CancellationToken);

        result.Ok.ShouldBeTrue();
        result.Data!.ResolvedPath.ShouldBe(anchor.Solution.FilePath);
        result.Data.Errors.ShouldNotBeNull();
        result.Data.Errors.First(e => e.Project == "Listed").File.ShouldBe("Listed/Program.cs");
    }
}
