using System.Collections.Immutable;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="VerificationService"/>'s baseline cache. The cost model this cache exists to
/// deliver — one compilation per verified edit rather than two — is only real if the cache actually
/// hits, and only <em>safe</em> if it misses whenever the source changed at all. Both halves are
/// asserted here against a counting decorator over the real compiler-only diagnostics pass.
/// </summary>
public class VerificationServiceCacheTests
{
    /// <summary>Mirrors <c>VerificationService.MaxCacheEntries</c> (internal, not visible to tests).</summary>
    private const int MaxCacheEntries = 16;

    /// <summary>Counts diagnostics passes while delegating to the real compiler-only computation.</summary>
    private sealed class CountingDiagnostics : IDiagnosticComputationService
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<DiagnosticComputationResult> GetDiagnosticsAsync(
            Project project, Compilation compilation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return DiagnosticComputationService.CompilerOnly.GetDiagnosticsAsync(project, compilation, cancellationToken);
        }

        public AnalyzerLoadReport DescribeAnalyzerLoad(Project project) =>
            DiagnosticComputationService.CompilerOnly.DescribeAnalyzerLoad(project);
    }

    private static (VerificationService Service, CountingDiagnostics Counter) CreateService()
    {
        var counter = new CountingDiagnostics();
        return (new VerificationService(A.Fake<ILogger<VerificationService>>(), counter), counter);
    }

    private static Solution WithText(Solution solution, string newText)
    {
        var document = solution.Projects.Single().Documents.Single();
        return solution.WithDocumentText(document.Id, SourceText.From(newText));
    }

    private const string Original = "public class Calc { public int Add(int a, int b) { return a + b; } }";

    [Fact]
    public async Task An_Unchanged_Baseline_Is_Compiled_Once_Across_Two_Verifications()
    {
        // Arrange
        var (workspace, project) = AdhocProjectBuilder.Create("Cached", [("Calc.cs", Original)]);
        using var _ = workspace;
        var (service, counter) = CreateService();
        using var __ = service;
        var baseline = project.Solution;
        var first = WithText(baseline, "public class Calc { public int Add(int a, int b) { return a + b + 1; } }");
        var second = WithText(baseline, "public class Calc { public int Add(int a, int b) { return a + b + 2; } }");

        // Act — two edits against the same on-disk state, the default previewOnly flow.
        await service.VerifyAsync(baseline, first, null, cancellationToken: TestContext.Current.CancellationToken);
        await service.VerifyAsync(baseline, second, null, cancellationToken: TestContext.Current.CancellationToken);

        // Assert — baseline + first + second = 3, not 4: the second call reused the baseline.
        counter.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task An_Identical_Verification_Is_A_Pure_Cache_Hit()
    {
        var (workspace, project) = AdhocProjectBuilder.Create("Same", [("Calc.cs", Original)]);
        using var _ = workspace;
        var (service, counter) = CreateService();
        using var __ = service;

        await service.VerifyAsync(null, project.Solution, null, cancellationToken: TestContext.Current.CancellationToken);
        await service.VerifyAsync(null, project.Solution, null, cancellationToken: TestContext.Current.CancellationToken);

        counter.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_Declaration_Change_Misses_The_Cache()
    {
        var (workspace, project) = AdhocProjectBuilder.Create("Decl", [("Calc.cs", Original)]);
        using var _ = workspace;
        var (service, counter) = CreateService();
        using var __ = service;
        var changed = WithText(project.Solution,
            "public class Calc { public int Add(int a, int b) { return a + b; } public int Sub() => 0; }");

        await service.VerifyAsync(null, project.Solution, null, cancellationToken: TestContext.Current.CancellationToken);
        await service.VerifyAsync(null, changed, null, cancellationToken: TestContext.Current.CancellationToken);

        counter.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task A_Body_Only_Change_Misses_The_Cache()
    {
        // Roslyn's dependent *semantic* version tracks consumable declarations, so a body-only edit
        // leaves it untouched. Keying the cache on that alone would serve a stale baseline the
        // moment a write changed a method body — and the next edit would be refused for an error it
        // did not cause. The key therefore carries the dependent version as well.
        var (workspace, project) = AdhocProjectBuilder.Create("Body", [("Calc.cs", Original)]);
        using var _ = workspace;
        var (service, counter) = CreateService();
        using var __ = service;
        var bodyChanged = WithText(project.Solution,
            "public class Calc { public int Add(int a, int b) { return Missing.Thing(); } }");

        // Measure the premise rather than assert it: the semantic version really is blind here, and
        // the dependent version really is not. If Roslyn ever changes that, this line says so.
        var ct = TestContext.Current.CancellationToken;
        var originalProject = project.Solution.Projects.Single();
        var editedProject = bodyChanged.Projects.Single();
        (await editedProject.GetDependentSemanticVersionAsync(ct))
            .ShouldBe(await originalProject.GetDependentSemanticVersionAsync(ct));
        (await editedProject.GetDependentVersionAsync(ct))
            .ShouldNotBe(await originalProject.GetDependentVersionAsync(ct));

        var before = await service.VerifyAsync(null, project.Solution, null, cancellationToken: TestContext.Current.CancellationToken);
        var after = await service.VerifyAsync(null, bodyChanged, null, cancellationToken: TestContext.Current.CancellationToken);

        counter.Calls.ShouldBe(2);
        before.Compiles.ShouldBe(true);
        after.Compiles.ShouldBe(false);
    }

    [Fact]
    public async Task A_Cached_Entry_Survives_Disposal_Of_The_Workspace_It_Came_From()
    {
        // Entries hold projected DiagnosticDetail values, never Diagnostic/Compilation/Location —
        // those would root a SyntaxTree into a workspace whose memory is never returned to the OS.
        var (workspace, project) = AdhocProjectBuilder.Create("Detached",
            [("Calc.cs", "public class Calc { public int A() => Missing.Thing(); }")]);
        var (service, counter) = CreateService();
        using var _ = service;
        var solution = project.Solution;

        var before = await service.VerifyAsync(null, solution, null, cancellationToken: TestContext.Current.CancellationToken);
        workspace.Dispose();

        var after = await service.VerifyAsync(null, solution, null, cancellationToken: TestContext.Current.CancellationToken);

        counter.Calls.ShouldBe(1);
        after.Errors.ShouldNotBeNull();
        after.Errors.Count.ShouldBe(before.Errors!.Count);
        after.Errors[0].Id.ShouldBe(before.Errors[0].Id);
        after.Errors[0].Message.ShouldBe(before.Errors[0].Message);
        after.Errors[0].Line.ShouldBe(before.Errors[0].Line);
    }

    [Fact]
    public async Task Concurrent_Misses_On_The_Same_Project_Compile_It_Once()
    {
        var (workspace, project) = AdhocProjectBuilder.Create("Racy", [("Calc.cs", Original)]);
        using var _ = workspace;
        var (service, counter) = CreateService();
        using var __ = service;
        var solution = project.Solution;

        // Act — tool invocations are not serialized, so two calls can land on a cold entry together.
        var verdicts = await Task.WhenAll(
            Task.Run(() => service.VerifyAsync(null, solution, null, cancellationToken: TestContext.Current.CancellationToken)),
            Task.Run(() => service.VerifyAsync(null, solution, null, cancellationToken: TestContext.Current.CancellationToken)));

        // Assert — single-flight, and no torn dictionary state.
        counter.Calls.ShouldBe(1);
        verdicts.ShouldAllBe(v => v.Compiles == true);
    }

    [Fact]
    public async Task The_Cache_Is_Bounded_And_Evicts_The_Least_Recently_Used_Entry()
    {
        var (workspace, project) = AdhocProjectBuilder.Create("Bounded", [("Calc.cs", Original)]);
        using var _ = workspace;
        var (service, counter) = CreateService();
        using var __ = service;
        var solution = project.Solution;

        // The very first state, which we come back to at the end.
        await service.VerifyAsync(null, solution, null, cancellationToken: TestContext.Current.CancellationToken);
        counter.Calls.ShouldBe(1);

        // Push it out with MaxCacheEntries distinct later states.
        for (var i = 0; i < MaxCacheEntries; i++)
        {
            var other = WithText(solution, $"public class Calc {{ public int Add(int a, int b) {{ return a + b + {i}; }} }}");
            await service.VerifyAsync(null, other, null, cancellationToken: TestContext.Current.CancellationToken);
        }

        counter.Calls.ShouldBe(MaxCacheEntries + 1);

        // Act — the original state is no longer cached, so it recompiles.
        await service.VerifyAsync(null, solution, null, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        counter.Calls.ShouldBe(MaxCacheEntries + 2);
    }
}
