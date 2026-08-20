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
}
