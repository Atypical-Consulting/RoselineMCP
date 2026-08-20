using System.Runtime.CompilerServices;
using System.Text.Json;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
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

    [Fact]
    public void Read_Returns_Null_For_No_Exception_At_All()
        => ResolvedPathStamp.Read(null).ShouldBeNull();

    /// <summary>
    /// A <c>ValidationError</c> is not automatically path-less. The rule is <em>where the failure
    /// was detected</em>, not which type it classified as: an <see cref="ArgumentException"/> raised
    /// by a service that had already loaded — <c>get_call_graph</c> handed a type instead of a
    /// method — classifies as <c>ValidationError</c> through <c>Error&lt;T&gt;</c> and carries the
    /// stamp, whereas one caught at the tool boundary before the service ran cannot.
    /// </summary>
    [Fact]
    public async Task ValidationError_Raised_After_The_Load_Still_Names_The_Checkout()
    {
        var (workspace, project) = AdhocProjectBuilder.Create(
            "Demo", [("Services.cs", "public class UserService { }")]);
        var navigationService = new CodeNavigationService(
            A.Fake<ILogger<CodeNavigationService>>(), AdhocProjectBuilder.FakeLoaderFor(workspace, project));

        // A type, not a method: rejected by the service *after* it resolved the symbol.
        var result = await GetCallGraphTool.GetCallGraph(navigationService, "UserService");

        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("ValidationError");
        result.Error.ResolvedPath.ShouldBe(project.FilePath);
    }

    /// <summary>
    /// The timeout case is the one where "which checkout answered?" matters most — being pointed at
    /// an unexpectedly large checkout is a leading cause of one — so the stamp must survive the
    /// <c>catch (OperationCanceledException)</c> arm too, which builds its envelope through
    /// <c>Cancellation&lt;T&gt;</c> rather than <c>Error&lt;T&gt;</c>.
    /// </summary>
    [Fact]
    public async Task Timeout_After_The_Load_Still_Names_The_Checkout()
    {
        const string SolutionPath = "/checkouts/main/Demo.sln";

        var result = await FindReferencesTool.FindReferences(
            StampingCancellingService(SolutionPath), "Whatever",
            options: Options.Create(new RoselineMcpOptions { DefaultTimeout = 1 }));

        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("TimeoutError");
        result.Error.ResolvedPath.ShouldBe(SolutionPath);
    }

    /// <summary>The caller-cancelled twin of the timeout case: same arm, same stamp.</summary>
    [Fact]
    public async Task Caller_Cancellation_After_The_Load_Still_Names_The_Checkout()
    {
        const string SolutionPath = "/checkouts/main/Demo.sln";
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await FindReferencesTool.FindReferences(
            StampingCancellingService(SolutionPath), "Whatever", cancellationToken: cts.Token);

        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("CancelledError");
        result.Error.ResolvedPath.ShouldBe(SolutionPath);
    }

    /// <summary>
    /// A service that has loaded and is now waiting: it observes the cancellation, stamps the
    /// checkout that was answering, and rethrows — exactly what the real services' catch arms do.
    /// </summary>
    private static ICodeNavigationService StampingCancellingService(string resolvedPath)
    {
        var navigationService = A.Fake<ICodeNavigationService>();
        A.CallTo(() => navigationService.FindReferencesAsync(
                A<string?>._, A<string>._, A<bool>._, A<int>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, call.GetArgument<CancellationToken>(4));
                }
                catch (Exception ex)
                {
                    ResolvedPathStamp.Stamp(ex, resolvedPath);
                    throw;
                }

                return new ReferencesResponse();
            });
        return navigationService;
    }

    /// <summary>
    /// The stamp is hand-placed at every site that loads a project, so a site added later can drop
    /// <c>resolvedPath</c> from its failures with the whole suite still green — nothing else would
    /// notice, because the omission is invisible on the success path. This pairs the two counts per
    /// file: every <c>projectLoader.LoadAsync</c> must be matched by exactly one
    /// <c>ResolvedPathStamp.Stamp</c> in the same file.
    /// </summary>
    /// <remarks>
    /// <c>CachingProjectLoader</c>'s own <c>_inner.LoadAsync</c> delegations are deliberately not
    /// matched: a failure *inside* loading resolved nothing, so it has no path to name, which is
    /// the absent case this feature is careful to preserve.
    /// </remarks>
    [Fact]
    public void Every_Site_That_Loads_A_Project_Also_Stamps_The_Path()
    {
        var sources = Directory.EnumerateFiles(RepoPath("RoselineMCP"), "*.cs", SearchOption.AllDirectories);
        var loadSites = 0;

        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            var loads = Occurrences(text, "projectLoader.LoadAsync(");
            if (loads == 0)
            {
                continue;
            }

            loadSites += loads;
            Occurrences(text, "ResolvedPathStamp.Stamp(").ShouldBe(
                loads,
                $"{Path.GetFileName(source)} loads a project {loads}x but does not stamp the resolved " +
                "path the same number of times — its failures would omit 'resolvedPath'.");
        }

        // Guards the guard: a rename of the loader would silently match nothing and pass.
        loadSites.ShouldBeGreaterThanOrEqualTo(12);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Resolves a repository-relative path from this source file's compile-time location, the same
    /// idiom <c>ReleaseWorkflowTests</c> uses. This file lives at <c>RoselineMCP.Tests/Tools/</c>,
    /// so the repository root is two levels up.
    /// </summary>
    private static string RepoPath(string relativePath, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", relativePath));
}
