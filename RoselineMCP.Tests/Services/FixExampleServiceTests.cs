using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class FixExampleServiceTests
{
    // CS0168 (declared, never used) fires on a and b; c and d are assigned, so they are clean sites
    // of the same shape (a variable declarator in a declaration in a local-declaration statement).
    private const string Source = """
        public class A
        {
            public void M()
            {
                int a;
                int b;
                int c;
                c = 1;
                int d;
                d = 2;
            }
        }
        """;

    private static (FixExampleService Sut, IDisposable Workspace) Create(
        string code = Source, bool fixable = false, IProjectLoader? loader = null)
    {
        var (workspace, project) = AdhocProjectBuilder.Create("Demo", [("A.cs", code)]);
        var filter = A.Fake<IDiagnosticFilterService>();
        A.CallTo(() => filter.IsFixableDiagnostic(A<string>._, A<Microsoft.CodeAnalysis.Project?>._)).Returns(fixable);
        var sut = new FixExampleService(
            A.Fake<ILogger<FixExampleService>>(),
            loader ?? AdhocProjectBuilder.FakeLoaderFor(workspace, project),
            DiagnosticComputationService.CompilerOnly,
            filter);
        return (sut, workspace);
    }

    [Fact]
    public async Task Resolves_The_Alert_And_Rule_Metadata()
    {
        var (sut, ws) = Create(fixable: true);
        using (ws)
        {
            var r = await sut.SuggestAsync(null, "CS0168", null, null, 3, 2000, TestContext.Current.CancellationToken);

            r.Rule.Id.ShouldBe("CS0168");
            r.Rule.Title.ShouldNotBeNullOrWhiteSpace();
            r.Alert.ShouldNotBeNull();
            r.Alert.File.ShouldBe("A.cs");
            r.Alert.Line.ShouldBe(5);
            r.Alert.Snippet.ShouldBe("int a;");
            r.HasFixer.ShouldBeTrue();
            r.Notes.ShouldContain(n => n.Contains("apply_fixes"));
        }
    }

    [Fact]
    public async Task Id_That_Never_Fires_Yields_No_Alert_And_One_Note()
    {
        var (sut, ws) = Create();
        using (ws)
        {
            var r = await sut.SuggestAsync(null, "CS9999", null, null, 3, 2000, TestContext.Current.CancellationToken);

            r.Alert.ShouldBeNull();
            r.Examples.ShouldBeEmpty();
            r.Notes.ShouldHaveSingleItem().ShouldContain("CS9999");
        }
    }

    [Fact]
    public async Task Returns_Clean_Shape_Matched_Sites_Excluding_Firing_Ones()
    {
        var (sut, ws) = Create();
        using (ws)
        {
            var r = await sut.SuggestAsync(null, "CS0168", null, null, 5, 2000, TestContext.Current.CancellationToken);

            r.Examples.Select(e => e.Line).ShouldBe([7, 9]);
            r.Examples.ShouldAllBe(e => e.File == "A.cs" && e.MatchedOn == "kinds");
            r.CandidatesScanned.ShouldBeGreaterThan(0);
            r.Truncated.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task MaxExamples_Truncates_And_Anchor_Selects_The_Occurrence()
    {
        var (sut, ws) = Create();
        using (ws)
        {
            var r = await sut.SuggestAsync(null, "CS0168", "A.cs", 6, 1, 2000, TestContext.Current.CancellationToken);

            r.Alert!.Line.ShouldBe(6);
            r.Examples.Count.ShouldBe(1);
            r.Truncated.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Unmatched_Anchor_Falls_Back_To_First_Occurrence_With_A_Note()
    {
        var (sut, ws) = Create();
        using (ws)
        {
            var r = await sut.SuggestAsync(null, "CS0168", "Nope.cs", 99, 3, 2000, TestContext.Current.CancellationToken);

            r.Alert!.Line.ShouldBe(5);
            r.Notes.ShouldContain(n => n.Contains("fell back"));
        }
    }

    [Fact]
    public async Task MaxCandidates_Bounds_The_Scan()
    {
        var (sut, ws) = Create();
        using (ws)
        {
            var r = await sut.SuggestAsync(null, "CS0168", null, null, 5, 2, TestContext.Current.CancellationToken);

            r.CandidatesScanned.ShouldBe(2);
            r.Truncated.ShouldBeTrue();
            r.Notes.ShouldContain(n => n.Contains("maxCandidates"));
        }
    }

    [Fact]
    public async Task No_Clean_Site_Yields_Empty_Examples_A_Note_And_The_Rule()
    {
        var (sut, ws) = Create("public class A { public void M() { int a; } }");
        using (ws)
        {
            var r = await sut.SuggestAsync(null, "CS0168", null, null, 3, 2000, TestContext.Current.CancellationToken);

            r.Examples.ShouldBeEmpty();
            r.Rule.Title.ShouldNotBeNullOrWhiteSpace();
            r.Notes.ShouldContain(n => n.Contains("No clean site"));
        }
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var (sut, ws) = Create();
        using (ws)
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Should.ThrowAsync<OperationCanceledException>(
                () => sut.SuggestAsync(null, "CS0168", null, null, 3, 2000, cts.Token));
        }
    }

    [Fact]
    public async Task Failure_Is_Stamped_With_The_Resolved_Path()
    {
        var (workspace, project) = AdhocProjectBuilder.Create("Demo", [("A.cs", Source)]);
        using (workspace)
        {
            var computation = A.Fake<IDiagnosticComputationService>();
            A.CallTo(() => computation.GetDiagnosticsAsync(A<Microsoft.CodeAnalysis.Project>._, A<Microsoft.CodeAnalysis.Compilation>._, A<CancellationToken>._))
                .Throws(new InvalidOperationException("boom"));
            var sut = new FixExampleService(
                A.Fake<ILogger<FixExampleService>>(),
                AdhocProjectBuilder.FakeLoaderFor(workspace, project, "/x/Demo.sln"),
                computation, A.Fake<IDiagnosticFilterService>());

            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => sut.SuggestAsync(null, "CS0168", null, null, 3, 2000, TestContext.Current.CancellationToken));

            ResolvedPathStamp.Read(ex).ShouldBe("/x/Demo.sln");
        }
    }
}
