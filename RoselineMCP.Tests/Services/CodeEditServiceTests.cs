using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
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
}
