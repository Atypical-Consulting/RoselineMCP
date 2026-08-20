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
/// Coverage for the FixAll (batch) fast path in <see cref="CodeFixService"/>: when a provider
/// ships a <see cref="FixAllProvider"/> supporting Project scope, every occurrence of a
/// diagnostic ID must be fixed in a single batch pass — instead of re-compiling the whole
/// project after each individual fix — while the response contract (FixedCount = occurrences
/// fixed, ChangedFiles, Notes) stays identical to the per-occurrence path. Exercised end-to-end
/// (real MSBuildWorkspace, real project on disk) with hand-written provider stubs injected
/// through a faked <see cref="ICodeFixProviderFactory"/> so the batch pass is observable.
/// </summary>
public class CodeFixServiceFixAllTests : IDisposable
{
    private readonly string _testDirectory;

    public CodeFixServiceFixAllTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodeFixServiceFixAll_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, true); } catch { /* ignored */ }
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
    /// Wraps the well-known batch fixer, counting how many times a fix-all pass is requested so a
    /// test can prove the batch path ran (and ran exactly once per diagnostic ID).
    /// </summary>
    private sealed class RecordingFixAllProvider : FixAllProvider
    {
        public int GetFixCalls;

        public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            GetFixCalls++;
            return await WellKnownFixAllProviders.BatchFixer.GetFixAsync(fixAllContext);
        }
    }

    /// <summary>
    /// A per-occurrence fix (removes the line the diagnostic sits on) with FixAll support via
    /// <see cref="WellKnownFixAllProviders.BatchFixer"/>. The optional
    /// <paramref name="fixableFileNameFragment"/> makes occurrences in other documents
    /// unfixable, so partial batch coverage can be simulated.
    /// </summary>
    private sealed class RemoveDiagnosticLineCodeFixProvider(string? fixableFileNameFragment = null) : CodeFixProvider
    {
        public RecordingFixAllProvider FixAll { get; } = new();

        public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("CS0219");

        public override FixAllProvider GetFixAllProvider() => FixAll;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            if (fixableFileNameFragment != null
                && !context.Document.Name.Contains(fixableFileNameFragment, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            var diagnostic = context.Diagnostics[0];
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Remove the line with the unused variable",
                    ct => RemoveLineAsync(context.Document, diagnostic, ct),
                    equivalenceKey: "RemoveUnusedLine"),
                diagnostic);

            return Task.CompletedTask;
        }

        private static async Task<Document> RemoveLineAsync(Document document, Diagnostic diagnostic, CancellationToken ct)
        {
            var text = await document.GetTextAsync(ct);
            var line = text.Lines.GetLineFromPosition(diagnostic.Location.SourceSpan.Start);
            var newText = text.Replace(TextSpan.FromBounds(line.Start, line.EndIncludingLineBreak), string.Empty);
            return document.WithText(newText);
        }
    }

    public class BatchPassTests : CodeFixServiceFixAllTests
    {
        [Fact]
        public async Task Should_Fix_All_Occurrences_In_A_Single_FixAll_Pass()
        {
            // Arrange — five CS0219 occurrences spread across two documents
            var csprojPath = CreateProject("Batch.csproj",
                ("FileA.cs", """
                 class FileA
                 {
                     static void MethodA()
                     {
                         int unusedA1 = 1;
                         int unusedA2 = 2;
                         int unusedA3 = 3;
                         System.Console.WriteLine("a");
                     }
                 }
                 """),
                ("FileB.cs", """
                 class FileB
                 {
                     static void MethodB()
                     {
                         int unusedB1 = 4;
                         int unusedB2 = 5;
                         System.Console.WriteLine("b");
                     }
                 }
                 """));

            var provider = new RemoveDiagnosticLineCodeFixProvider();
            var factory = A.Fake<ICodeFixProviderFactory>();
            A.CallTo(() => factory.GetProviderForDiagnostic("CS0219")).Returns(provider);

            var sut = CreateSut(factory);

            // Act
            var result = await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false);

            // Assert — the response contract is identical to the per-occurrence path...
            result.FixedCount.ShouldBe(5);
            result.FixersApplied.ShouldBe(["CS0219"]);
            result.ChangedFiles.ShouldBe(["FileA.cs", "FileB.cs"], ignoreOrder: true);
            result.Patch.ShouldNotBeNullOrWhiteSpace();
            result.Notes.ShouldContain(n => n.Contains("Applied 5 fixes to 2 files"));

            // ...but all five occurrences went through ONE batch pass, not five recompiles.
            provider.FixAll.GetFixCalls.ShouldBe(1);

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            var fileA = await File.ReadAllTextAsync(Path.Combine(projectDir, "FileA.cs"));
            var fileB = await File.ReadAllTextAsync(Path.Combine(projectDir, "FileB.cs"));
            fileA.ShouldNotContain("unusedA");
            fileB.ShouldNotContain("unusedB");
            fileA.ShouldContain("System.Console.WriteLine(\"a\");");
            fileB.ShouldContain("System.Console.WriteLine(\"b\");");
        }
    }

    public class PartialBatchCoverageTests : CodeFixServiceFixAllTests
    {
        [Fact]
        public async Task Should_Fall_Back_To_Occurrence_Loop_When_Batch_Probe_Occurrence_Is_Unfixable()
        {
            // Arrange — the FIRST occurrence (by ordinal file path, "A..." before "B...") is
            // unfixable, so the batch path cannot even obtain an equivalence key and must hand
            // everything to the per-occurrence fallback, which fixes the "B..." occurrence.
            var csprojPath = CreateProject("PartialBatch.csproj",
                ("AUnfixable.cs", """
                 class First
                 {
                     static void MethodA()
                     {
                         int unusedA = 1;
                         System.Console.WriteLine("a");
                     }
                 }
                 """),
                ("BFixable.cs", """
                 class Second
                 {
                     static void MethodB()
                     {
                         int unusedB = 2;
                         System.Console.WriteLine("b");
                     }
                 }
                 """));

            var provider = new RemoveDiagnosticLineCodeFixProvider(fixableFileNameFragment: "BFixable");
            var factory = A.Fake<ICodeFixProviderFactory>();
            A.CallTo(() => factory.GetProviderForDiagnostic("CS0219")).Returns(provider);

            var sut = CreateSut(factory);

            // Act
            var result = await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false);

            // Assert — same outcome the per-occurrence path always guaranteed.
            result.FixedCount.ShouldBe(1);
            result.ChangedFiles.ShouldBe(["BFixable.cs"]);

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            (await File.ReadAllTextAsync(Path.Combine(projectDir, "AUnfixable.cs"))).ShouldContain("unusedA");
            (await File.ReadAllTextAsync(Path.Combine(projectDir, "BFixable.cs"))).ShouldNotContain("unusedB");
        }
    }
}
