using System.Diagnostics;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using RoselineMCP.Tests.Services;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

/// <summary>
/// Issue #254: with an absolute <c>project</c> inside a linked git worktree, the tools must answer
/// from that worktree, not from the main checkout whose workspace may already be cached. Runs the
/// real <see cref="CachingProjectLoader"/> + <see cref="ProjectLoader"/> against a real
/// <c>git worktree add</c> made in a temp directory (never in this repository).
/// </summary>
public class WorktreeProjectResolutionTests : IDisposable
{
    private const string CodeBehind = "Components/Pages/Index.razor.cs";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RoselineWorktree_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { /* ignored: read-only .git objects on Windows */ }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(true)]   // nested at <main>/.claude/worktrees/wt, the layout in the report
    [InlineData(false)]  // sibling of the main checkout
    public async Task Explicit_Worktree_Project_Is_Answered_From_The_Worktree(bool nested)
    {
        var main = Path.Combine(_root, "main");
        var worktree = nested
            ? Path.Combine(main, ".claude", "worktrees", "wt")
            : Path.Combine(_root, "wt");
        CreateRepoWithWorktree(main, worktree);

        var mainCsproj = Path.Combine(main, "src", "Hub", "Hub.csproj");
        var wtCsproj = Path.Combine(worktree, "src", "Hub", "Hub.csproj");
        File.WriteAllText(Path.Combine(worktree, "src", "Hub", "Components", "Pages", "Index.razor.cs"),
            "namespace Hub; public partial class Index { public void OnlyInWorktree() { } }");

        using var loader = new CachingProjectLoader(
            new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), new MSBuildService(A.Fake<ILogger<MSBuildService>>())),
            Options.Create(new RoselineMcpOptions()),
            A.Fake<ILogger<CachingProjectLoader>>());
        var service = new CodeNavigationService(A.Fake<ILogger<CodeNavigationService>>(), loader);

        // Load the main checkout first so its workspace is cached, as in the report.
        var mainOutline = await SearchSymbolsTool.SearchSymbols(service, file: CodeBehind, project: mainCsproj);
        mainOutline.Ok.ShouldBeTrue(mainOutline.Error?.Message);
        mainOutline.Data!.Symbols.Select(s => s.Name).ShouldContain("OnlyInMain");

        var outline = await SearchSymbolsTool.SearchSymbols(service, file: CodeBehind, project: wtCsproj);
        outline.Ok.ShouldBeTrue(outline.Error?.Message);
        var names = outline.Data!.Symbols.Select(s => s.Name).ToList();
        names.ShouldContain("OnlyInWorktree");
        names.ShouldNotContain("OnlyInMain");
        Path.GetFullPath(outline.Data.ResolvedPath).ShouldStartWith(Path.GetFullPath(worktree));

        var info = await GetSymbolInfoTool.GetSymbolInfo(service, "Hub.Index.OnlyInWorktree", project: wtCsproj);
        info.Ok.ShouldBeTrue(info.Error?.Message);
        info.Data!.Source.ShouldNotBeNull().ShouldContain("OnlyInWorktree");
    }

    [Fact]
    public void FileNotFoundMessage_Is_Unchanged_Without_Load_Failures_And_Bounded_With_Them()
    {
        CodeNavigationService.FileNotFoundMessage("A.cs", [])
            .ShouldBe("File not found in the loaded solution: A.cs");

        var message = CodeNavigationService.FileNotFoundMessage("A.cs", ["one", "two", "three", "four", new string('x', 1000)]);
        message.ShouldContain("5 load failure(s)");
        message.ShouldContain("one | two | three");
        message.ShouldNotContain("four");
        message.Length.ShouldBeLessThan(400);
    }

    [Fact]
    public async Task Missing_ProjectReference_Surfaces_As_A_Workspace_Failure()
    {
        var dir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "Broken.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Missing\Missing.csproj" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { }");

        var loader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), new MSBuildService(A.Fake<ILogger<MSBuildService>>()));
        using var loaded = await loader.LoadAsync(csproj);

        loaded.WorkspaceFailures.ShouldNotBeEmpty();
        loaded.WorkspaceFailures.Count.ShouldBeLessThanOrEqualTo(LoadedProject.MaxWorkspaceFailures);
    }

    private void CreateRepoWithWorktree(string main, string worktree)
    {
        Directory.CreateDirectory(_root);
        if (!Git(_root, "--version"))
        {
            Assert.Skip("git is not available.");
        }

        var proj = Path.Combine(main, "src", "Hub");
        Directory.CreateDirectory(Path.Combine(proj, "Components", "Pages"));
        SolutionFileBuilder.Write(Path.Combine(main, "Hub.sln"), ("Hub", "src/Hub/Hub.csproj"));
        File.WriteAllText(Path.Combine(proj, "Hub.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(proj, "Components", "Pages", "Index.razor"), "<h1>Hub</h1>");
        File.WriteAllText(Path.Combine(proj, CodeBehind),
            "namespace Hub; public partial class Index { public void OnlyInMain() { } }");

        Git(main, "init", "-q").ShouldBeTrue();
        Git(main, "add", "-A").ShouldBeTrue();
        Git(main, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "-m", "init").ShouldBeTrue();
        Git(main, "worktree", "add", "-q", "--detach", worktree).ShouldBeTrue();
    }

    private static bool Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
