using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using RoselineMCP.Tests.Services;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

/// <summary>
/// Covers the failure half of the "which checkout answered?" disclosure. #143 put
/// <c>resolvedPath</c> on every success response; the shape the bug was actually filed about is a
/// <em>failure</em> — an agent working in a git worktree omits <c>project</c>, the server answers
/// from the main checkout, and the symbol is not there. That produces
/// <c>Symbol not found: 'X'</c>, not a wrong-but-successful answer, so the path has to reach the
/// failure envelope too or the miss stays undiagnosable.
/// </summary>
/// <remarks>
/// The distinction these tests defend is <b>absent vs empty</b>. A failure that never resolved a
/// project must omit the field entirely: <c>""</c> would read as "resolved to nothing", which is a
/// different claim from "never resolved". Asserting <see langword="null"/> on the object does not
/// prove that, so the wire shape is asserted directly.
/// </remarks>
public class ToolErrorResolvedPathTests
{
    private const string MissingSymbol = "NoSuchSymbolAnywhere";

    /// <summary>Serializes the envelope the way the MCP SDK does, to assert the wire shape.</summary>
    private static string SerializeError<T>(ToolResult<T> result) =>
        JsonSerializer.Serialize(result.Error);

    [Fact]
    public async Task Symbol_Not_Found_Names_The_Checkout_That_Answered()
    {
        var (workspace, project) = AdhocProjectBuilder.Create(
            "Demo", [("Services.cs", "public class UserService { }")]);
        var navigationService = new CodeNavigationService(
            A.Fake<ILogger<CodeNavigationService>>(), AdhocProjectBuilder.FakeLoaderFor(workspace, project));

        var result = await FindReferencesTool.FindReferences(navigationService, MissingSymbol);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("NotFoundError");
        // The whole point: the response names the checkout, so "not here" and "wrong checkout" are
        // now distinguishable. Two checkouts of one repo are otherwise reported identically.
        result.Error.ResolvedPath.ShouldBe(project.FilePath);
        SerializeError(result).ShouldContain("resolvedPath");
    }

    [Fact]
    public async Task Solution_Path_Wins_Over_The_Project_Path_When_One_Was_Loaded()
    {
        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
            [("Demo", [("Services.cs", "public class UserService { }")])],
            solutionFileName: "Demo.sln");
        var navigationService = new CodeNavigationService(
            A.Fake<ILogger<CodeNavigationService>>(), AdhocProjectBuilder.FakeLoaderFor(workspace, anchor));

        var result = await FindReferencesTool.FindReferences(navigationService, MissingSymbol);

        result.Error.ShouldNotBeNull();
        result.Error.ResolvedPath.ShouldBe(anchor.Solution.FilePath);
    }

    [Fact]
    public async Task Failure_Before_Any_Project_Resolved_Omits_The_Field_Entirely()
    {
        var navigationService = A.Fake<ICodeNavigationService>();
        A.CallTo(() => navigationService.FindReferencesAsync(
                A<string?>._, A<string>._, A<bool>._, A<int>._, A<CancellationToken>._))
            // No stamp: this is what a load failure or a pre-resolution validation error looks like.
            .Throws(new FileNotFoundException("Project not found: Ghost"));

        var result = await FindReferencesTool.FindReferences(navigationService, "Whatever");

        result.Error.ShouldNotBeNull();
        result.Error.ResolvedPath.ShouldBeNull();
        // Absent, not "". An empty string would claim "resolved to nothing"; nothing was resolved.
        SerializeError(result).ShouldNotContain("resolvedPath");
    }

    [Fact]
    public async Task ValidationError_Detected_Before_The_Service_Runs_Omits_The_Field()
    {
        var navigationService = A.Fake<ICodeNavigationService>();

        // Neither 'query' nor 'file': rejected at the tool boundary, so the service is never
        // invoked and no project was ever resolved. ValidationError takes no exception and so has
        // no stamp to read — absent is the accurate answer here, not a gap.
        var result = await SearchSymbolsTool.SearchSymbols(navigationService);

        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("ValidationError");
        result.Error.ResolvedPath.ShouldBeNull();
        SerializeError(result).ShouldNotContain("resolvedPath");
    }

    [Fact]
    public async Task InternalError_Still_Scrubs_Its_Message_And_Still_Names_The_Checkout()
    {
        const string SecretPath = "/checkouts/worktree/Demo.sln";
        var boom = new NullReferenceException("Object reference not set to an instance of an object.");
        ResolvedPathStamp.Stamp(boom, SecretPath);

        var navigationService = A.Fake<ICodeNavigationService>();
        A.CallTo(() => navigationService.FindReferencesAsync(
                A<string?>._, A<string>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .Throws(boom);

        var result = await FindReferencesTool.FindReferences(navigationService, "Whatever");

        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("InternalError");
        result.Error.Message.ShouldNotContain("Object reference");
        // The path is not part of what InternalError scrubs: the same absolute path already rides
        // every success response, and it is the one field that makes the failure diagnosable.
        result.Error.ResolvedPath.ShouldBe(SecretPath);
    }

    [Fact]
    public void Stamp_Ignores_A_Path_That_Would_Serialize_As_Empty()
    {
        var ex = new InvalidOperationException("boom");

        ResolvedPathStamp.Stamp(ex, string.Empty);

        // An in-memory solution has no path; "nothing to report" must not become "resolved to nothing".
        ResolvedPathStamp.Read(ex).ShouldBeNull();
    }

    [Fact]
    public void Stamp_Keeps_The_Innermost_Path_When_Frames_Nest()
    {
        var ex = new InvalidOperationException("boom");

        ResolvedPathStamp.Stamp(ex, "/inner/Inner.sln");
        ResolvedPathStamp.Stamp(ex, "/outer/Outer.sln");

        // The frame closest to the throw is the one that actually answered the call.
        ResolvedPathStamp.Read(ex).ShouldBe("/inner/Inner.sln");
    }

    [Fact]
    public void Read_Returns_Null_For_An_Unstamped_Exception()
        => ResolvedPathStamp.Read(new InvalidOperationException("boom")).ShouldBeNull();
}
