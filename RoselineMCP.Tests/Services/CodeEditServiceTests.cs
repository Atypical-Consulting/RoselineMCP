using System.Text;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Integration tests for <see cref="CodeEditService"/> that run the real Roslyn edit/rename logic
/// against in-memory <see cref="AdhocWorkspace"/> projects, plus one on-disk apply test proving the
/// write path only touches files when <c>previewOnly</c> is explicitly false.
/// </summary>
public class CodeEditServiceTests
{
    private static CodeEditService CreateService(AdhocWorkspace workspace, Project project)
    {
        var loader = AdhocProjectBuilder.FakeLoaderFor(workspace, project);
        return new CodeEditService(A.Fake<ILogger<CodeEditService>>(), loader, new DiffService());
    }

    private static CodeEditService CreateService(string projectName, params (string Name, string Code)[] files)
    {
        var (workspace, project) = AdhocProjectBuilder.Create(projectName, files);
        return CreateService(workspace, project);
    }

    [Fact]
    public async Task EditMember_Replace_Produces_Diff_Without_Writing()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } }"));

        var result = await service.EditMemberAsync(
            "Demo", "Add", "replace", "public int Add(int a, int b) { return a + b + 0; }",
            previewOnly: true, CancellationToken.None);

        result.Operation.ShouldBe("replace");
        result.PreviewOnly.ShouldBeTrue();
        result.Applied.ShouldBeFalse();
        result.Patch.ShouldNotBeNullOrEmpty();
        result.ChangedFiles.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task EditMember_Add_Inserts_New_Member()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } }"));

        var result = await service.EditMemberAsync(
            "Demo", "Calc", "add", "public int Zero() { return 0; }",
            previewOnly: true, CancellationToken.None);

        result.Operation.ShouldBe("add");
        result.Patch.ShouldContain("Zero");
    }

    [Fact]
    public async Task EditMember_Delete_Removes_Member()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } public int Sub(int a, int b) { return a - b; } }"));

        var result = await service.EditMemberAsync(
            "Demo", "Sub", "delete", null, previewOnly: true, CancellationToken.None);

        result.Operation.ShouldBe("delete");
        result.Patch.ShouldContain("Sub");
    }

    [Fact]
    public async Task EditMember_Delete_Field_Removes_Whole_Declaration()
    {
        var service = CreateService("Demo", ("Foo.cs",
            "public class Foo { private int _counter; public int Value() { return 0; } }"));

        var result = await service.EditMemberAsync(
            "Demo", "_counter", "delete", null, previewOnly: true, CancellationToken.None);

        result.Operation.ShouldBe("delete");
        result.Patch.ShouldContain("_counter");
        // Must not leave a dangling `private int ;` — the whole field declaration is removed.
        result.Patch.ShouldNotContain("private int ;");
    }

    [Fact]
    public async Task EditMember_Delete_One_Of_Several_Fields_Keeps_The_Others()
    {
        var service = CreateService("Demo", ("Foo.cs",
            "public class Foo { private int _a, _b; }"));

        var result = await service.EditMemberAsync(
            "Demo", "_a", "delete", null, previewOnly: true, CancellationToken.None);

        result.Patch.ShouldNotBeNullOrEmpty();
        result.Patch.ShouldNotContain("private int ;");
    }

    [Fact]
    public async Task EditMember_Replace_Field_Produces_Diff()
    {
        var service = CreateService("Demo", ("Foo.cs",
            "public class Foo { private int _x = 1; }"));

        var result = await service.EditMemberAsync(
            "Demo", "_x", "replace", "private int _x = 2;", previewOnly: true, CancellationToken.None);

        result.Operation.ShouldBe("replace");
        result.Patch.ShouldContain("2");
    }

    [Fact]
    public async Task EditMember_Replace_Keeps_New_Doc_Comment_From_NewSource()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc {\n    /// <summary>Old.</summary>\n    public int Add(int a, int b) { return a + b; }\n}"));

        var result = await service.EditMemberAsync(
            "Demo", "Add", "replace",
            "/// <summary>New and improved.</summary>\npublic int Add(int a, int b) { return a + b; }",
            previewOnly: true, CancellationToken.None);

        result.Patch.ShouldContain("New and improved");
    }

    [Fact]
    public async Task EditMember_Invalid_NewSource_Throws_ArgumentException()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } }"));

        await Should.ThrowAsync<ArgumentException>(() => service.EditMemberAsync(
            "Demo", "Add", "replace", "public int (((", previewOnly: true, CancellationToken.None));
    }

    [Fact]
    public async Task EditMember_Add_To_Non_Type_Throws_ArgumentException()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } }"));

        await Should.ThrowAsync<ArgumentException>(() => service.EditMemberAsync(
            "Demo", "Add", "add", "public int X() => 1;", previewOnly: true, CancellationToken.None));
    }

    [Fact]
    public async Task RenameSymbol_Preview_Produces_Diff_Without_Writing()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } public int Twice(int x) { return Add(x, x); } }"));

        var result = await service.RenameSymbolAsync("Demo", "Add", "Sum", previewOnly: true, cancellationToken: CancellationToken.None);

        result.NewName.ShouldBe("Sum");
        result.PreviewOnly.ShouldBeTrue();
        result.Applied.ShouldBeFalse();
        result.Patch.ShouldContain("Sum");
        result.ChangedFiles.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RenameSymbol_Resolves_Symbol_In_Unreferenced_Sibling_Project()
    {
        // The anchor project (App) does not reference Lib — resolution must still find Lib's
        // Widget because the whole solution is searched, not just the anchor.
        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
        [
            ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
            ("Lib", [("Widget.cs", "namespace LibNs { public class Widget { } }")])
        ]);
        var service = CreateService(workspace, anchor);

        var result = await service.RenameSymbolAsync("App", "Widget", "Gadget", previewOnly: true, cancellationToken: CancellationToken.None);

        result.NewName.ShouldBe("Gadget");
        result.Patch.ShouldContain("Gadget");
        result.ChangedFiles.ShouldNotBeEmpty();
    }

    /// <summary>
    /// Emitted paths are solution-root-relative with forward slashes — the same base the
    /// navigation tools and ApplyFixes use — so the same file has one canonical path across every
    /// tool's output. Pinned against a multi-project solution whose FilePath is set (mirroring an
    /// MSBuild-loaded .sln).
    /// </summary>
    [Fact]
    public async Task RenameSymbol_Paths_Are_Solution_Root_Relative_In_Multi_Project_Solution()
    {
        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
            [
                ("App", [("App.cs", "namespace AppNs { public class AppRoot { } }")]),
                ("Lib", [("Widget.cs", "namespace LibNs { public class Widget { } }")])
            ],
            solutionFileName: "Everything.sln");
        var service = CreateService(workspace, anchor);

        var result = await service.RenameSymbolAsync("App", "Widget", "Gadget", previewOnly: true, cancellationToken: CancellationToken.None);

        // The changed file lives in the sibling Lib project; its path is relative to the
        // solution root (not the anchor project directory) and uses forward slashes.
        result.ChangedFiles.ShouldBe(["Lib/Widget.cs"]);
        result.Patch.ShouldContain("a/Lib/Widget.cs");
        result.Patch.ShouldContain("b/Lib/Widget.cs");
    }

    [Fact]
    public async Task EditMember_Paths_Are_Solution_Root_Relative_In_Multi_Project_Solution()
    {
        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
            [
                ("App", [("Calc.cs", "namespace AppNs { public class Calc { public int Add(int a, int b) { return a + b; } } }")]),
                ("Lib", [("Widget.cs", "namespace LibNs { public class Widget { } }")])
            ],
            solutionFileName: "Everything.sln");
        var service = CreateService(workspace, anchor);

        var result = await service.EditMemberAsync(
            "App", "Add", "replace", "public int Add(int a, int b) { return a + b + 1; }",
            previewOnly: true, CancellationToken.None);

        result.ChangedFiles.ShouldBe(["App/Calc.cs"]);
        result.Patch.ShouldContain("a/App/Calc.cs");
    }

    [Fact]
    public async Task RenameSymbol_Reports_Strictly_Increasing_Progress()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } public int Twice(int x) { return Add(x, x); } }"));

        var reports = new List<ProgressNotificationValue>();
        var progress = A.Fake<IProgress<ProgressNotificationValue>>();
        A.CallTo(() => progress.Report(A<ProgressNotificationValue>._))
            .Invokes((ProgressNotificationValue v) => reports.Add(v));

        await service.RenameSymbolAsync("Demo", "Add", "Sum", previewOnly: true, progress, CancellationToken.None);

        // The load/resolve/rename phases each report progress, and the value must strictly increase
        // (MCP requirement).
        reports.Count.ShouldBeGreaterThanOrEqualTo(3);
        for (var i = 1; i < reports.Count; i++)
        {
            reports[i].Progress.ShouldBeGreaterThan(reports[i - 1].Progress);
        }
    }

    [Fact]
    public async Task RenameSymbol_Invalid_Identifier_Throws_ArgumentException()
    {
        var service = CreateService("Demo", ("Calc.cs",
            "public class Calc { public int Add(int a, int b) { return a + b; } }"));

        await Should.ThrowAsync<ArgumentException>(
            () => service.RenameSymbolAsync("Demo", "Add", "123bad", previewOnly: true, cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task RenameSymbol_Apply_Writes_Updated_File_To_Disk()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "roseline-edit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDirectory);
        var code = "public class Calc { public int Add(int a, int b) { return a + b; } public int Twice(int x) { return Add(x, x); } }";
        var filePath = Path.Combine(baseDirectory, "Calc.cs");
        await File.WriteAllTextAsync(filePath, code);

        try
        {
            var (workspace, project) = AdhocProjectBuilder.Create("Demo", [("Calc.cs", code)], baseDirectory);
            var service = CreateService(workspace, project);

            var result = await service.RenameSymbolAsync("Demo", "Add", "Sum", previewOnly: false, cancellationToken: CancellationToken.None);

            result.Applied.ShouldBeTrue();
            var updated = await File.ReadAllTextAsync(filePath);
            updated.ShouldContain("Sum");
            updated.ShouldNotContain("Add");
        }
        finally
        {
            Directory.Delete(baseDirectory, recursive: true);
        }
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Runs an on-disk apply scenario against a file written (and loaded) with the given encoding
    /// and returns the resulting raw bytes, so each write path can assert the encoding survived.
    /// </summary>
    private static async Task<byte[]> RunApplyAsync(
        Encoding encoding,
        Func<CodeEditService, Task> applyAsync)
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "roseline-edit-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(baseDirectory);
        var code = "public class Calc { public int Add(int a, int b) { return a + b; } public int Twice(int x) { return Add(x, x); } }";
        var filePath = Path.Combine(baseDirectory, "Calc.cs");
        await File.WriteAllTextAsync(filePath, code, encoding);

        try
        {
            var (workspace, project) = AdhocProjectBuilder.Create("Demo", [("Calc.cs", code)], baseDirectory, encoding);
            var service = CreateService(workspace, project);

            await applyAsync(service);

            return await File.ReadAllBytesAsync(filePath);
        }
        finally
        {
            Directory.Delete(baseDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EditMember_Apply_Preserves_Utf8_Bom()
    {
        var bytes = await RunApplyAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), async service =>
        {
            var result = await service.EditMemberAsync(
                "Demo", "Add", "replace", "public int Add(int a, int b) { return a + b + 1; }",
                previewOnly: false, CancellationToken.None);
            result.Applied.ShouldBeTrue();
        });

        bytes.Take(3).ShouldBe(Utf8Bom, customMessage: "the UTF-8 BOM must be preserved on write");
        Encoding.UTF8.GetString(bytes).ShouldContain("a + b + 1");
    }

    [Fact]
    public async Task RenameSymbol_Apply_Preserves_Utf8_Bom()
    {
        var bytes = await RunApplyAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), async service =>
        {
            var result = await service.RenameSymbolAsync(
                "Demo", "Add", "Sum", previewOnly: false, cancellationToken: CancellationToken.None);
            result.Applied.ShouldBeTrue();
        });

        bytes.Take(3).ShouldBe(Utf8Bom, customMessage: "the UTF-8 BOM must be preserved on write");
        Encoding.UTF8.GetString(bytes).ShouldContain("Sum");
    }

    [Fact]
    public async Task RenameSymbol_Apply_Preserves_Utf16_Encoding()
    {
        var bytes = await RunApplyAsync(Encoding.Unicode, async service =>
        {
            var result = await service.RenameSymbolAsync(
                "Demo", "Add", "Sum", previewOnly: false, cancellationToken: CancellationToken.None);
            result.Applied.ShouldBeTrue();
        });

        // UTF-16 LE byte order mark — the file must not have been re-encoded as UTF-8.
        bytes.Take(2).ShouldBe([(byte)0xFF, (byte)0xFE], customMessage: "the UTF-16 LE BOM must be preserved on write");
        Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).ShouldContain("Sum");
    }

    /// <summary>
    /// The write tools are where a wrong checkout costs the most — an edit lands in the main
    /// checkout while the agent believes it is in an isolated worktree. The absolute resolved path
    /// is the only field that distinguishes them, so both write payloads must carry it.
    /// </summary>
    [Fact]
    public async Task EditMember_ReportsTheResolvedProjectPath_InPreview()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "roseline-tests", Guid.NewGuid().ToString("n"));
        var (workspace, project) = AdhocProjectBuilder.Create(
            "Acme", [("A.cs", "public class Widget { public int X => 1; }")], baseDir);
        using (workspace)
        {
            var service = CreateService(workspace, project);

            var response = await service.EditMemberAsync(
                null, "Widget.X", "replace", "public int X => 2;", previewOnly: true, CancellationToken.None);

            response.ResolvedPath.ShouldBe(Path.Combine(baseDir, "Acme.csproj"));
        }
    }

    [Fact]
    public async Task RenameSymbol_ReportsTheResolvedProjectPath_InPreview()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "roseline-tests", Guid.NewGuid().ToString("n"));
        var (workspace, project) = AdhocProjectBuilder.Create(
            "Acme", [("A.cs", "public class Widget { public int X => 1; }")], baseDir);
        using (workspace)
        {
            var service = CreateService(workspace, project);

            var response = await service.RenameSymbolAsync(
                null, "Widget.X", "Y", previewOnly: true, cancellationToken: CancellationToken.None);

            response.ResolvedPath.ShouldBe(Path.Combine(baseDir, "Acme.csproj"));
        }
    }
}
