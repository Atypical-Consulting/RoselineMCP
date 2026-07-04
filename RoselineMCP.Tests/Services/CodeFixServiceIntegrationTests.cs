using System.Text;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// End-to-end integration tests for <see cref="CodeFixService.ApplyFixesAsync"/> that load a
/// real project from disk via a real <see cref="MSBuildService"/> and apply real Roslyn code
/// fix providers discovered by a real <see cref="CodeFixProviderFactory"/>.
///
/// These tests exist specifically to prove that removing the "async void" registerCodeFix
/// callback (previously fired-and-forgotten inside <c>CodeFixContext</c>) did not just fix
/// the compiler warning but produces a solution whose <c>FixedCount</c> and on-disk file
/// content are actually consistent with each other for multi-diagnostic and multi-document
/// fix runs — including the exact "two overlapping fixable diagnostics in one document"
/// scenario that the async-void race could corrupt.
/// </summary>
public class CodeFixServiceIntegrationTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly CodeFixService _sut;

    public CodeFixServiceIntegrationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodeFixServiceIntegration_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        var logger = A.Fake<ILogger<CodeFixService>>();
        var analyzerService = A.Fake<ISolutionAnalyzerService>();
        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        var codeFixProviderFactory = new CodeFixProviderFactory(A.Fake<ILogger<CodeFixProviderFactory>>());
        var diffService = new DiffService();

        _sut = new CodeFixService(logger, analyzerService, codeFixProviderFactory, diffService, msBuildService);
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

    /// <summary>
    /// Writes a minimal SDK-style project (no PackageReferences, so MSBuildWorkspace can
    /// design-time-build it without a prior `dotnet restore`) plus the given source files.
    /// Each project gets its own subdirectory so that SDK-style implicit compile globbing
    /// (`**/*.cs`) never pulls files from a sibling project created in the same test.
    /// </summary>
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

    public class SingleDiagnosticTests : CodeFixServiceIntegrationTests
    {
        [Fact]
        public async Task Should_Apply_Fix_And_Write_Corrected_Content_To_Disk()
        {
            // Arrange — one CS0219 (assigned but never used local)
            var csprojPath = CreateProject("Single.csproj",
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

            // Capture progress reports emitted while fixes are applied.
            var reports = new List<ProgressNotificationValue>();
            var progress = A.Fake<IProgress<ProgressNotificationValue>>();
            A.CallTo(() => progress.Report(A<ProgressNotificationValue>._))
                .Invokes((ProgressNotificationValue v) => reports.Add(v));

            // Act
            var result = await _sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false, progress: progress);

            // Assert
            result.FixedCount.ShouldBe(1);
            result.FixersApplied.ShouldContain("CS0219");
            result.ChangedFiles.ShouldContain("Program.cs");
            result.Patch.ShouldNotBeNullOrWhiteSpace();
            result.PreviewOnly.ShouldBeFalse();

            // Progress was reported against the total number of diagnostic IDs requested.
            reports.ShouldNotBeEmpty();
            reports.ShouldContain(r => r.Total == 1);
            reports.ShouldContain(r => r.Progress >= 1);

            var onDisk = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(csprojPath)!, "Program.cs"));
            onDisk.ShouldNotContain("unused");
            onDisk.ShouldContain("System.Console.WriteLine(\"hi\");");
        }

        [Fact]
        public async Task Should_Not_Write_To_Disk_When_PreviewOnly()
        {
            // Arrange
            var csprojPath = CreateProject("Preview.csproj",
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
            var programPath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Program.cs");
            var originalContent = await File.ReadAllTextAsync(programPath);

            // Act
            var result = await _sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: true);

            // Assert — the fix was computed (patch/count reflect it) but disk is untouched
            result.FixedCount.ShouldBe(1);
            result.PreviewOnly.ShouldBeTrue();
            result.Notes.ShouldContain(n => n.Contains("Preview mode"));

            var onDisk = await File.ReadAllTextAsync(programPath);
            onDisk.ShouldBe(originalContent);
        }

        [Fact]
        public async Task Should_Return_Empty_Response_When_No_Ids_Provided()
        {
            // Arrange — a loadable project, but an empty ids list (the MCP tool layer rejects
            // this before the service is reached; the service itself just does nothing).
            var csprojPath = CreateProject("NoIds.csproj",
                ("Program.cs", """
                 class Program
                 {
                     static void Main()
                     {
                         System.Console.WriteLine("hi");
                     }
                 }
                 """));

            // Act
            var result = await _sut.ApplyFixesAsync(csprojPath, [], previewOnly: true);

            // Assert
            result.ShouldNotBeNull();
            result.PreviewOnly.ShouldBeTrue();
            result.FixersApplied.ShouldBeEmpty();
            result.FixedCount.ShouldBe(0);
            result.ChangedFiles.ShouldBeEmpty();
        }

        [Fact]
        public async Task Should_Add_Note_When_No_Matching_Diagnostics_Exist()
        {
            // Arrange — no unused variables at all
            var csprojPath = CreateProject("Clean.csproj",
                ("Program.cs", """
                 class Program
                 {
                     static void Main()
                     {
                         System.Console.WriteLine("hi");
                     }
                 }
                 """));

            // Act
            var result = await _sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: true);

            // Assert
            result.FixedCount.ShouldBe(0);
            result.FixersApplied.ShouldBeEmpty();
            result.Notes.ShouldContain(n => n.Contains("No diagnostics found for CS0219"));
        }
    }

    /// <summary>
    /// Regression coverage for the original async-void race: two fixable diagnostics of the
    /// SAME id in the SAME document, fixed in a single ApplyFixesAsync call. Under the old
    /// "async void" registerCodeFix callback, the outer loop's `await RegisterCodeFixesAsync`
    /// returned before the callback's continuation actually applied the change, so the second
    /// diagnostic's fix (and even the first, depending on scheduling) could be computed against
    /// stale document state and/or be silently dropped by the "last write wins" assignment to
    /// `currentSolution` — leaving `fixCount` overstated relative to what actually landed in
    /// the solution/file. This test proves that no longer happens, and that it doesn't happen
    /// *by luck* by repeating the whole run multiple times.
    /// </summary>
    public class OverlappingDiagnosticsInOneDocumentRegressionTests : CodeFixServiceIntegrationTests
    {
        [Fact]
        public async Task Should_Deterministically_Fix_Both_Overlapping_Diagnostics_In_One_Document()
        {
            for (var run = 0; run < 5; run++)
            {
                // Arrange — two independent unused locals (CS0219) in the same method/document
                var csprojPath = CreateProject($"Overlap{run}.csproj",
                    ($"Overlap{run}.cs", """
                     class Program
                     {
                         static void Main()
                         {
                             int unusedA = 1;
                             int unusedB = 2;
                             System.Console.WriteLine("hi");
                         }
                     }
                     """));

                // Act
                var result = await _sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false);

                // Assert — fixCount must equal exactly the number of diagnostics that actually
                // disappeared from the file, every single run.
                result.FixedCount.ShouldBe(2, $"run {run}: fixCount should reflect both fixes");
                result.ChangedFiles.ShouldContain($"Overlap{run}.cs");

                var onDisk = await File.ReadAllTextAsync(
                    Path.Combine(Path.GetDirectoryName(csprojPath)!, $"Overlap{run}.cs"));
                onDisk.ShouldNotContain("unusedA", customMessage: $"run {run}: first fix should have landed");
                onDisk.ShouldNotContain("unusedB", customMessage: $"run {run}: second fix should have landed");
                onDisk.ShouldContain("System.Console.WriteLine(\"hi\");");
            }
        }

        [Fact]
        public async Task Should_Fix_Mixed_CS0168_And_CS0219_In_Same_Document()
        {
            // Arrange — CS0168 (declared, never used) AND CS0219 (assigned, never used)
            // on adjacent lines of the same document, requested as two separate ids.
            var csprojPath = CreateProject("Mixed.csproj",
                ("Mixed.cs", """
                 class Program
                 {
                     static void Main()
                     {
                         int unusedDeclared;
                         int unusedAssigned = 2;
                         System.Console.WriteLine("hi");
                     }
                 }
                 """));

            // Act
            var result = await _sut.ApplyFixesAsync(csprojPath, ["CS0168", "CS0219"], previewOnly: false);

            // Assert
            result.FixedCount.ShouldBe(2);
            result.FixersApplied.ShouldBe(["CS0168", "CS0219"], ignoreOrder: true);

            var onDisk = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(csprojPath)!, "Mixed.cs"));
            onDisk.ShouldNotContain("unusedDeclared");
            onDisk.ShouldNotContain("unusedAssigned");
        }
    }

    /// <summary>
    /// The disk-write path must re-encode a fixed file with the encoding it was originally read
    /// with. Writing with a plain <c>File.WriteAllTextAsync(path, string)</c> always emitted
    /// BOM-less UTF-8, silently stripping a UTF-8 BOM (or re-encoding UTF-16) on every applied fix.
    /// </summary>
    public class EncodingPreservationTests : CodeFixServiceIntegrationTests
    {
        private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

        [Fact]
        public async Task Should_Preserve_Utf8_Bom_When_Writing_Fixed_File()
        {
            // Arrange — a source file explicitly written with a UTF-8 BOM
            var csprojPath = CreateProject("BomRoundTrip.csproj");
            var programPath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Program.cs");
            await File.WriteAllTextAsync(programPath, """
                class Program
                {
                    static void Main()
                    {
                        int unused = 1;
                        System.Console.WriteLine("hi");
                    }
                }
                """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            // Sanity: the BOM really is on disk before the fix runs.
            (await File.ReadAllBytesAsync(programPath)).Take(3).ShouldBe(Utf8Bom);

            // Act
            var result = await _sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false);

            // Assert — the fix landed AND the BOM survived the rewrite
            result.FixedCount.ShouldBe(1);
            var bytes = await File.ReadAllBytesAsync(programPath);
            bytes.Take(3).ShouldBe(Utf8Bom, customMessage: "the UTF-8 BOM must be preserved on write");

            var onDisk = await File.ReadAllTextAsync(programPath);
            onDisk.ShouldNotContain("unused");
            onDisk.ShouldContain("System.Console.WriteLine(\"hi\");");
        }
    }

    public class MultiDocumentTests : CodeFixServiceIntegrationTests
    {
        [Fact]
        public async Task Should_Apply_Fixes_Across_Multiple_Documents()
        {
            // Arrange — one unused variable per file, two files, two diagnostic ids, same project
            var csprojPath = CreateProject(
                "Multi.csproj",
                ("FileA.cs", """
                 class FileA
                 {
                     static void MethodA()
                     {
                         int unusedInA;
                         System.Console.WriteLine("a");
                     }
                 }
                 """),
                ("FileB.cs", """
                 class FileB
                 {
                     static void MethodB()
                     {
                         int unusedInB = 5;
                         System.Console.WriteLine("b");
                     }
                 }
                 """));

            // Act
            var result = await _sut.ApplyFixesAsync(csprojPath, ["CS0168", "CS0219"], previewOnly: false);

            // Assert
            result.FixedCount.ShouldBe(2);
            result.ChangedFiles.ShouldContain("FileA.cs");
            result.ChangedFiles.ShouldContain("FileB.cs");
            result.FixersApplied.ShouldBe(["CS0168", "CS0219"], ignoreOrder: true);

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            var fileA = await File.ReadAllTextAsync(Path.Combine(projectDir, "FileA.cs"));
            var fileB = await File.ReadAllTextAsync(Path.Combine(projectDir, "FileB.cs"));
            fileA.ShouldNotContain("unusedInA");
            fileB.ShouldNotContain("unusedInB");
        }

        /// <summary>
        /// Several occurrences of the same two diagnostic IDs, spread over two documents, fixed
        /// by the real compiler fix providers in one call. With the FixAll (batch) fast path,
        /// each ID is fixed in a single pass rather than one full re-analysis per occurrence —
        /// the response contract (FixedCount = occurrences fixed) must be unchanged.
        /// </summary>
        [Fact]
        public async Task Should_Fix_Many_Occurrences_Of_Both_Ids_Across_Files()
        {
            // Arrange — FileA: 2× CS0219 + 1× CS0168; FileB: 1× CS0219 + 1× CS0168
            var csprojPath = CreateProject(
                "Many.csproj",
                ("FileA.cs", """
                 class FileA
                 {
                     static void MethodA()
                     {
                         int assignedA1 = 1;
                         int assignedA2 = 2;
                         int declaredA;
                         System.Console.WriteLine("a");
                     }
                 }
                 """),
                ("FileB.cs", """
                 class FileB
                 {
                     static void MethodB()
                     {
                         int assignedB = 3;
                         int declaredB;
                         System.Console.WriteLine("b");
                     }
                 }
                 """));

            // Act
            var result = await _sut.ApplyFixesAsync(csprojPath, ["CS0219", "CS0168"], previewOnly: false);

            // Assert
            result.FixedCount.ShouldBe(5);
            result.FixersApplied.ShouldBe(["CS0219", "CS0168"], ignoreOrder: true);
            result.ChangedFiles.ShouldContain("FileA.cs");
            result.ChangedFiles.ShouldContain("FileB.cs");

            var projectDir = Path.GetDirectoryName(csprojPath)!;
            var fileA = await File.ReadAllTextAsync(Path.Combine(projectDir, "FileA.cs"));
            var fileB = await File.ReadAllTextAsync(Path.Combine(projectDir, "FileB.cs"));
            fileA.ShouldNotContain("assignedA1");
            fileA.ShouldNotContain("assignedA2");
            fileA.ShouldNotContain("declaredA");
            fileB.ShouldNotContain("assignedB");
            fileB.ShouldNotContain("declaredB");
            fileA.ShouldContain("System.Console.WriteLine(\"a\");");
            fileB.ShouldContain("System.Console.WriteLine(\"b\");");
        }
    }
}
