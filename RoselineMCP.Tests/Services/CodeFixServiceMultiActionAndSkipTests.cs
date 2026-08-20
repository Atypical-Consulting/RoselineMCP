using System.Collections.Immutable;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Regression coverage for two <see cref="CodeFixService"/> bugs found in
/// <c>ApplyFixesForDiagnosticIdAsync</c>, exercised end-to-end (real MSBuildWorkspace, real
/// project on disk) with hand-written <see cref="CodeFixProvider"/> stubs injected through a
/// faked <see cref="ICodeFixProviderFactory"/> so the exact multi-action / unfixable-occurrence
/// scenarios can be reproduced deterministically:
///
/// 1. A diagnostic occurrence with 2+ registered <see cref="CodeAction"/>s must only have the
///    FIRST one applied — applying every registered action overwrites earlier edits with later
///    ones while over-counting a single occurrence as multiple successful fixes.
/// 2. An occurrence that yields no usable code action must be skipped on its own, not treated as
///    a reason to abandon every other occurrence of the same diagnostic ID.
/// </summary>
public class CodeFixServiceMultiActionAndSkipTests : IDisposable
{
    private readonly string _testDirectory;

    public CodeFixServiceMultiActionAndSkipTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodeFixServiceMultiAction_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_testDirectory, true); }
        catch { /* ignored */ }
    }

    private const string MinimalCsprojXml =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private string CreateProject(string projectFileName, params (string FileName, string Content)[] files)
    {
        var projectDir = Path.Combine(_testDirectory, Path.GetFileNameWithoutExtension(projectFileName));
        Directory.CreateDirectory(projectDir);

        var csprojPath = Path.Combine(projectDir, projectFileName);
        File.WriteAllText(csprojPath, MinimalCsprojXml);

        foreach (var (fileName, content) in files)
        {
            File.WriteAllText(Path.Combine(projectDir, fileName), content);
        }

        return csprojPath;
    }

    private static CodeFixService CreateSut(ICodeFixProviderFactory factory)
    {
        var logger = A.Fake<ILogger<CodeFixService>>();
        var analyzerService = A.Fake<ISolutionAnalyzerService>();
        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        var diffService = new DiffService();
        var projectLoader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuildService);

        return new CodeFixService(logger, analyzerService, factory, diffService, projectLoader, TestVerification.New());
    }

    /// <summary>
    /// Registers two competing <see cref="CodeAction"/>s for the same diagnostic occurrence,
    /// each replacing the whole document with a distinct, distinguishable marker so the test can
    /// assert exactly which one (if any) actually landed.
    /// </summary>
    private sealed class TwoActionsCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("CS0219");

        public override FixAllProvider? GetFixAllProvider() => null;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics[0];

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Replace with FIRST marker",
                    _ => Task.FromResult(context.Document.WithText(SourceText.From("// FIRST\n")))),
                diagnostic);

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Replace with SECOND marker",
                    _ => Task.FromResult(context.Document.WithText(SourceText.From("// SECOND\n")))),
                diagnostic);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Only offers a fix for documents whose name contains <paramref name="fixableFileNameFragment"/>;
    /// for every other document it registers nothing at all, simulating a provider that declines
    /// to offer a fix for that particular occurrence.
    /// </summary>
    private sealed class FixesOnlyNamedFileCodeFixProvider(string fixableFileNameFragment, string correctedContent) : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("CS0219");

        public override FixAllProvider? GetFixAllProvider() => null;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            if (!context.Document.Name.Contains(fixableFileNameFragment, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            var diagnostic = context.Diagnostics[0];
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Remove unused variable",
                    _ => Task.FromResult(context.Document.WithText(SourceText.From(correctedContent)))),
                diagnostic);

            return Task.CompletedTask;
        }
    }

    public class MultipleCodeActionsPerOccurrenceTests : CodeFixServiceMultiActionAndSkipTests
    {
        [Fact]
        public async Task Should_Apply_Only_First_Registered_CodeAction_For_A_Single_Occurrence()
        {
            // Arrange — one CS0219 occurrence, but the fix provider registers TWO competing
            // CodeActions for it (mirroring, e.g., an ambiguous CS0246 resolvable via two
            // candidate namespaces).
            var csprojPath = CreateProject("MultiAction.csproj",
                ("Program.cs", """
                 class Program
                 {
                     static void Main()
                     {
                         int unused = 1;
                         System.Console.WriteLine("hi");
                     }
                 }
                 """));

            var factory = A.Fake<ICodeFixProviderFactory>();
            A.CallTo(() => factory.GetProviderForDiagnostic("CS0219")).Returns(new TwoActionsCodeFixProvider());

            var sut = CreateSut(factory);

            // Act
            // allowIntroducedErrors: this fixture's "fix" replaces the whole file with a comment,
            // deleting Main — so the compile gate would (correctly) refuse it. The question here is
            // which registered action lands, not whether the result builds.
            var result = await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false, allowIntroducedErrors: true, cancellationToken: TestContext.Current.CancellationToken);

            // Assert — exactly one fix applied for the one occurrence, not one per action.
            result.FixedCount.ShouldBe(1);
            result.FixersApplied.ShouldContain("CS0219");

            // Only the FIRST action's edit should have landed; the second must not have
            // silently overwritten it.
            var onDisk = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(csprojPath)!, "Program.cs"), TestContext.Current.CancellationToken);
            onDisk.ShouldBe("// FIRST\n");
        }
    }

    /// <summary>
    /// Registers a fix whose <see cref="CodeAction"/> replaces the document with text that is
    /// byte-identical to what is already there — simulating a fixer whose net effect is a no-op
    /// (or one whose edit is undone by the formatting pass that follows). Roslyn's changed-document
    /// tracking is version-based, not content-based, so <c>operation.ChangedSolution</c> still marks
    /// the document as touched even though nothing textually changed.
    /// </summary>
    private sealed class NoOpCodeFixProvider : CodeFixProvider
    {
        // The fake "fix" never actually removes the unused-variable diagnostic, so without this
        // guard the occurrence-by-occurrence loop would keep re-matching the same location and
        // re-apply the no-op edit up to its iteration bound. Registering nothing the second time
        // marks the location unfixable and lets the loop terminate after exactly one application —
        // which is what the scenario under test (one fixer run, one touched-but-unchanged document)
        // needs.
        private bool _applied;

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("CS0219");

        public override FixAllProvider? GetFixAllProvider() => null;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            if (_applied)
            {
                return Task.CompletedTask;
            }
            _applied = true;

            var diagnostic = context.Diagnostics[0];
            context.RegisterCodeFix(
                CodeAction.Create(
                    "No-op fix",
                    async ct => context.Document.WithText(await context.Document.GetTextAsync(ct))),
                diagnostic);

            return Task.CompletedTask;
        }
    }

    public class NoOpCodeActionDoesNotCountAsChangedTests : CodeFixServiceMultiActionAndSkipTests
    {
        [Fact]
        public async Task A_No_Op_Fix_Does_Not_Populate_ChangedFiles_Or_The_Patch()
        {
            // Regression for #162: response.ChangedFiles used to be populated from Roslyn's
            // changed-document set BEFORE the diff was computed, so a fixer that touches a document
            // without changing its text still made HasChanges (ChangedFiles.Count > 0) true — and
            // the write-confirmation gate would ask a human to approve a write that produces
            // nothing. ChangedFiles must only ever reflect a real, non-blank diff.
            const string source = """
                class Program
                {
                    static void Main()
                    {
                        int unused = 1;
                        System.Console.WriteLine("hi");
                    }
                }
                """;
            var csprojPath = CreateProject("NoOpFix.csproj", ("Program.cs", source));

            var factory = A.Fake<ICodeFixProviderFactory>();
            A.CallTo(() => factory.GetProviderForDiagnostic("CS0219")).Returns(new NoOpCodeFixProvider());

            var sut = CreateSut(factory);

            var result = await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: true, cancellationToken: TestContext.Current.CancellationToken);

            result.FixedCount.ShouldBe(1, "the fixer DID run and register a change to the solution");
            result.ChangedFiles.ShouldBeEmpty("nothing textually changed, so nothing should count as changed");
            result.Patch.ShouldBeNullOrWhiteSpace();
            result.HasChanges.ShouldBeFalse();
        }
    }

    public class UnfixableOccurrenceDoesNotAbortOthersTests : CodeFixServiceMultiActionAndSkipTests
    {
        [Fact]
        public async Task Should_Skip_Unfixable_Occurrence_And_Still_Fix_Later_Occurrence_In_Different_File()
        {
            // Arrange — two occurrences of CS0219 in two different files. The provider only
            // offers a fix for the SECOND file; the earliest occurrence (by ordinal file path,
            // "A..." sorts before "B...") is unfixable.
            const string fixedSecondFileContent =
                """
                class Second
                {
                    static void MethodB()
                    {
                        System.Console.WriteLine("b");
                    }
                }
                """;

            var csprojPath = CreateProject("SkipUnfixable.csproj",
                ("AFirstUnfixable.cs", """
                 class First
                 {
                     static void MethodA()
                     {
                         int unusedA = 1;
                         System.Console.WriteLine("a");
                     }
                 }
                 """),
                ("BSecondFixable.cs", """
                 class Second
                 {
                     static void MethodB()
                     {
                         int unusedB = 2;
                         System.Console.WriteLine("b");
                     }
                 }
                 """));

            var factory = A.Fake<ICodeFixProviderFactory>();
            A.CallTo(() => factory.GetProviderForDiagnostic("CS0219"))
                .Returns(new FixesOnlyNamedFileCodeFixProvider("Second", fixedSecondFileContent));

            var sut = CreateSut(factory);

            // Act
            var result = await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false, cancellationToken: TestContext.Current.CancellationToken);

            // Assert — the unfixable first occurrence did not abort the fixable second one.
            result.FixedCount.ShouldBe(1);
            result.FixersApplied.ShouldContain("CS0219");
            result.ChangedFiles.ShouldContain("BSecondFixable.cs");
            result.ChangedFiles.ShouldNotContain("AFirstUnfixable.cs");

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            var fileA = await File.ReadAllTextAsync(Path.Combine(projectDir, "AFirstUnfixable.cs"), TestContext.Current.CancellationToken);
            var fileB = await File.ReadAllTextAsync(Path.Combine(projectDir, "BSecondFixable.cs"), TestContext.Current.CancellationToken);
            fileA.ShouldContain("unusedA", customMessage: "the unfixable occurrence must be left untouched");
            fileB.ShouldNotContain("unusedB", customMessage: "the fixable occurrence in the other file must still be fixed");
        }
    }
}
