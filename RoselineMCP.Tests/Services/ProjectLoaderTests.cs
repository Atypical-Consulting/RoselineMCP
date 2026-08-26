using System.Reflection;
using System.Runtime.Versioning;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="ProjectLoader"/>'s reference-resolution and auto-discovery behavior.
///
/// The pure resolution logic (<c>ResolveTargetPath</c>) is exercised directly against an isolated,
/// hermetic temp directory tree — no MSBuild, no <c>Directory.SetCurrentDirectory</c> — so the
/// auto-discovery/ambiguity cases are deterministic and parallel-safe. A single MSBuild integration
/// test then proves that a real <c>.sln</c> path is accepted end-to-end and yields a usable project.
/// </summary>
public class ProjectLoaderTests : IDisposable
{
    private readonly string _root;
    private readonly string _baseDir;

    public ProjectLoaderTests()
    {
        // Nest the base directory a few levels deep under a fresh root so the parent-directory portion
        // of auto-discovery stays inside this test's own (empty) tree and can never pick up a stray
        // .sln/.csproj from the machine.
        _root = Path.Combine(Path.GetTempPath(), $"RoselineProjectLoader_{Guid.NewGuid():N}");
        _baseDir = Path.Combine(_root, "a", "b", "work");
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, true); }
        catch { /* ignored */ }
    }

    /// <summary>Invokes the private static <c>ResolveTargetPath</c>, unwrapping reflection's exception wrapper.</summary>
    private static string ResolveTargetPath(string? project, string baseDirectory)
    {
        var method = typeof(ProjectLoader).GetMethod(
            "ResolveTargetPath", BindingFlags.NonPublic | BindingFlags.Static)!;

        try
        {
            return (string)method.Invoke(null, [project, baseDirectory])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private string Touch(string relativePath)
    {
        var fullPath = Path.Combine(_baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Empty);
        return fullPath;
    }

    /// <summary>Creates an empty file relative to the test <b>root</b> (an ancestor of the base directory), for parent-level discovery cases.</summary>
    private string TouchAtRoot(string relativePath)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, string.Empty);
        return fullPath;
    }

    [Fact]
    public void AutoDiscover_Finds_Single_Csproj_When_No_Project_Given()
    {
        var csproj = Touch("Lib.csproj");

        var resolved = ResolveTargetPath(null, _baseDir);

        resolved.ShouldBe(Path.GetFullPath(csproj));
    }

    [Fact]
    public void AutoDiscover_Prefers_Sln_Over_Csproj()
    {
        Touch("App.csproj");
        var sln = Touch("App.sln");

        var resolved = ResolveTargetPath(null, _baseDir);

        resolved.ShouldBe(Path.GetFullPath(sln));
    }

    [Fact]
    public void AutoDiscover_Searches_Immediate_Subdirectories()
    {
        var csproj = Touch(Path.Combine("src", "Nested.csproj"));

        var resolved = ResolveTargetPath(null, _baseDir);

        resolved.ShouldBe(Path.GetFullPath(csproj));
    }

    [Fact]
    public void AutoDiscover_Throws_Actionable_Error_When_Multiple_Solutions_Found()
    {
        Touch("One.sln");
        Touch("Two.sln");

        var ex = Should.Throw<ArgumentException>(() => ResolveTargetPath(null, _baseDir));

        ex.Message.ShouldContain("multiple");
        ex.Message.ShouldContain("One.sln");
        ex.Message.ShouldContain("Two.sln");
        ex.Message.ShouldContain("explicit 'project'");
    }

    /// <summary>
    /// Regression guard for the real-world repro: a git worktree whose working directory has its
    /// own <c>.sln</c> while an ancestor (the main checkout, here the 3rd parent) has one too.
    /// The nearest level must win — this used to fail as "ambiguous".
    /// </summary>
    [Fact]
    public void AutoDiscover_Prefers_The_Cwd_Solution_Over_A_Parent_Solution()
    {
        var cwdSln = Touch("Work.sln");
        TouchAtRoot("Main.sln"); // _root is the 3rd parent of _baseDir (_root/a/b/work)

        var resolved = ResolveTargetPath(null, _baseDir);

        resolved.ShouldBe(Path.GetFullPath(cwdSln));
    }

    [Fact]
    public void AutoDiscover_Falls_Back_To_The_Nearest_Parent_Solution_When_The_Cwd_Is_Empty()
    {
        var parentSln = TouchAtRoot(Path.Combine("a", "b", "Parent.sln")); // 1st parent of _baseDir

        var resolved = ResolveTargetPath(null, _baseDir);

        resolved.ShouldBe(Path.GetFullPath(parentSln));
    }

    [Fact]
    public void AutoDiscover_Ambiguity_Error_Lists_Only_The_Nearest_Levels_Candidates()
    {
        Touch("One.sln");
        Touch("Two.sln");
        TouchAtRoot("Far.sln"); // a farther level never contributes to the ambiguity

        var ex = Should.Throw<ArgumentException>(() => ResolveTargetPath(null, _baseDir));

        ex.Message.ShouldContain("One.sln");
        ex.Message.ShouldContain("Two.sln");
        ex.Message.ShouldNotContain("Far.sln");
    }

    [Fact]
    public void AutoDiscover_Prefers_The_Cwd_Csproj_Over_A_Parent_Csproj()
    {
        var cwdCsproj = Touch("Lib.csproj");
        TouchAtRoot("Outer.csproj");

        var resolved = ResolveTargetPath(null, _baseDir);

        resolved.ShouldBe(Path.GetFullPath(cwdCsproj));
    }

    [Fact]
    public void AutoDiscover_Throws_Actionable_Error_When_Nothing_Found()
    {
        var ex = Should.Throw<ArgumentException>(() => ResolveTargetPath(null, _baseDir));

        ex.Message.ShouldContain("auto-discover");
        ex.Message.ShouldContain("explicit 'project'");
    }

    [Fact]
    public void ResolveTargetPath_Accepts_An_Explicit_Sln_Path()
    {
        var sln = Touch("Explicit.sln");

        // A .sln path that previously failed resolution is now returned as the solution to open.
        var resolved = ResolveTargetPath(sln, _baseDir);

        resolved.ShouldBe(Path.GetFullPath(sln));
    }

    [Fact]
    public void ResolveTargetPath_Accepts_An_Explicit_Csproj_Path()
    {
        var csproj = Touch("Lib.csproj");

        var resolved = ResolveTargetPath(csproj, _baseDir);

        resolved.ShouldBe(csproj);
    }

    /// <summary>Invokes the private static <c>FindProjectInSolution</c> used to select the requested project inside a loaded solution.</summary>
    private static Microsoft.CodeAnalysis.Project? FindProjectInSolution(
        Microsoft.CodeAnalysis.Solution solution, string projectPath, string? projectName)
    {
        var method = typeof(ProjectLoader).GetMethod(
            "FindProjectInSolution", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Microsoft.CodeAnalysis.Project?)method.Invoke(null, [solution, projectPath, projectName]);
    }

    /// <summary>
    /// Regression guard for the old ListDiagnostics/ApplyFixes resolution copy, which matched
    /// projects by <c>FilePath.Contains(projectName)</c> — so asking for "Foo" could select
    /// "FooBar". The shared loader selects by exact (case-insensitive) name only.
    /// </summary>
    [Fact]
    public void FindProjectInSolution_Selects_By_Exact_Name_Not_Substring()
    {
        // FooBar is added FIRST, and its file path contains "Foo" — the old substring match
        // would have returned it for the query "Foo".
        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
        [
            ("FooBar", [("A.cs", "public class A { }")]),
            ("Foo", [("B.cs", "public class B { }")])
        ]);
        using var _1 = workspace;

        var match = FindProjectInSolution(anchor.Solution, "/nonexistent/query.csproj", "Foo");

        match.ShouldNotBeNull();
        match!.Name.ShouldBe("Foo");
    }

    [Fact]
    public void FindProjectInSolution_Returns_Null_For_A_Name_That_Only_Matches_As_Substring()
    {
        var (workspace, anchor) = AdhocProjectBuilder.CreateSolution(
        [
            ("FooBar", [("A.cs", "public class A { }")])
        ]);
        using var _1 = workspace;

        // "Foo" is a substring of FooBar's name/path but matches no project's exact name.
        var match = FindProjectInSolution(anchor.Solution, "/nonexistent/query.csproj", "Foo");

        match.ShouldBeNull();
    }

    /// <summary>
    /// End-to-end: a real <c>.sln</c> file (referencing a real SDK-style project) is loaded via a real
    /// <see cref="MSBuildService"/> and yields a usable primary project whose solution contains the
    /// referenced project — the core of fix (A), that passing a solution path no longer fails.
    /// </summary>
    [Fact]
#pragma warning disable xUnit1051 // TestContext.Current not needed here
    public async Task LoadAsync_Accepts_A_Sln_Path_And_Returns_A_Usable_Project()
    {
        var slnPath = CreateRealSolution();

        var loader = new ProjectLoader(
            A.Fake<ILogger<ProjectLoader>>(),
            new MSBuildService(A.Fake<ILogger<MSBuildService>>()));

        using var loaded = await loader.LoadAsync(slnPath);

        loaded.Project.ShouldNotBeNull();
        loaded.Project.Name.ShouldBe("App");
        loaded.Solution.Projects.ShouldContain(p => p.Name == "App");

        // The loaded project is genuinely usable: it compiles and exposes the declared type.
        var compilation = await loaded.Project.GetCompilationAsync();
        compilation.ShouldNotBeNull();
        compilation!.GetTypeByMetadataName("App.Widget").ShouldNotBeNull();
    }
#pragma warning restore xUnit1051

    private const string MinimalCsprojXml =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// Writes a minimal SDK-style project (no PackageReferences, so MSBuildWorkspace can design-time
    /// build it offline) plus a hand-written <c>.sln</c> that references it, and returns the .sln path.
    /// </summary>
    private string CreateRealSolution()
    {
        var projectDir = Path.Combine(_baseDir, "App");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"), MinimalCsprojXml);
        File.WriteAllText(Path.Combine(projectDir, "Widget.cs"), "namespace App { public class Widget { } }");

        return SolutionFileBuilder.Write(Path.Combine(_baseDir, "App.sln"), "App");
    }

    /// <summary>
    /// Writes two minimal SDK-style projects sharing one hand-written <c>.sln</c>: <c>Main</c> is
    /// listed in it, <c>Scratch</c> is on disk but NOT listed — the exact layout from the
    /// <c>resolvedPath</c> bug (issue #151), where <c>FindProjectInSolution</c> misses the anchor
    /// and <c>LoadAsync</c> falls through to <c>OpenProjectAsync</c> on the already-open workspace.
    /// </summary>
    private (string SlnPath, string MainCsprojPath, string ScratchCsprojPath) CreateRealSolutionWithUnlistedProject()
    {
        var mainDir = Path.Combine(_baseDir, "Main");
        Directory.CreateDirectory(mainDir);
        var mainCsprojPath = Path.Combine(mainDir, "Main.csproj");
        File.WriteAllText(mainCsprojPath, MinimalCsprojXml);
        File.WriteAllText(Path.Combine(mainDir, "MainWidget.cs"), "namespace Main { public class MainWidget { } }");

        var scratchDir = Path.Combine(_baseDir, "Scratch");
        Directory.CreateDirectory(scratchDir);
        var scratchCsprojPath = Path.Combine(scratchDir, "Scratch.csproj");
        File.WriteAllText(scratchCsprojPath, MinimalCsprojXml);
        File.WriteAllText(Path.Combine(scratchDir, "ScratchWidget.cs"), "namespace Scratch { public class ScratchWidget { } }");

        var slnPath = SolutionFileBuilder.Write(Path.Combine(_baseDir, "Repo.sln"), "Main");

        return (slnPath, mainCsprojPath, scratchCsprojPath);
    }

    /// <summary>Writes a minimal SDK-style project with no <c>.sln</c> anywhere in its ancestry.</summary>
    private string CreateStandaloneProject(string name = "Standalone")
    {
        var projectDir = Path.Combine(_baseDir, name);
        Directory.CreateDirectory(projectDir);
        var csprojPath = Path.Combine(projectDir, $"{name}.csproj");
        File.WriteAllText(csprojPath, MinimalCsprojXml);
        File.WriteAllText(Path.Combine(projectDir, "Widget.cs"), $"namespace {name} {{ public class Widget {{ }} }}");
        return csprojPath;
    }

    /// <summary>
    /// Regression for #151: a <c>.csproj</c> not listed in its nearest ancestor <c>.sln</c> is
    /// opened standalone, and <c>resolvedPath</c> must report THAT file — not the <c>.sln</c>,
    /// which never contributed it.
    /// </summary>
    [Fact]
#pragma warning disable xUnit1051 // TestContext.Current not needed here
    public async Task LoadAsync_ReportsTheCsproj_WhenItIsNotListedInTheAncestorSln()
    {
        var (_, _, scratchCsprojPath) = CreateRealSolutionWithUnlistedProject();
        var loader = new ProjectLoader(
            A.Fake<ILogger<ProjectLoader>>(),
            new MSBuildService(A.Fake<ILogger<MSBuildService>>()));

        using var loaded = await loader.LoadAsync(scratchCsprojPath);

        loaded.Project.Name.ShouldBe("Scratch");
        loaded.ResolvedPath.ShouldBe(scratchCsprojPath);
    }

    /// <summary>Companion to the regression above: a project genuinely listed in the <c>.sln</c> still reports it.</summary>
    [Fact]
    public async Task LoadAsync_ReportsTheSln_WhenTheProjectIsListedInIt()
    {
        var (slnPath, mainCsprojPath, _) = CreateRealSolutionWithUnlistedProject();
        var loader = new ProjectLoader(
            A.Fake<ILogger<ProjectLoader>>(),
            new MSBuildService(A.Fake<ILogger<MSBuildService>>()));

        using var loaded = await loader.LoadAsync(mainCsprojPath);

        loaded.Project.Name.ShouldBe("Main");
        loaded.ResolvedPath.ShouldBe(slnPath);
    }

    /// <summary>
    /// Regression for #213: an explicitly-named <c>.csproj</c> must not fail to load merely because
    /// some ancestor directory holds two real, unrelated <c>.sln</c> files. The caller already named
    /// the exact project — only its solution context is ambiguous, not the project itself — so
    /// <c>LoadAsync</c> degrades to the same standalone-project fallback it already takes when a
    /// resolved <c>.sln</c> simply doesn't list the target project, rather than propagating
    /// <c>FindSolutionFile</c>'s ambiguity refusal.
    /// </summary>
    [Fact]
    public async Task LoadAsync_FallsBackToStandalone_WhenAnExplicitCsprojHitsAnAmbiguousAncestorSolution()
    {
        var csprojPath = Touch(Path.Combine("Src", "MyLib", "MyLib.csproj"));
        File.WriteAllText(csprojPath, MinimalCsprojXml);
        Touch("Full.sln");
        Touch("ClientOnly.sln");

        var loader = new ProjectLoader(
            A.Fake<ILogger<ProjectLoader>>(),
            new MSBuildService(A.Fake<ILogger<MSBuildService>>()));

        using var loaded = await loader.LoadAsync(csprojPath);

        loaded.Project.Name.ShouldBe("MyLib");
        loaded.ResolvedPath.ShouldBe(csprojPath);
    }

    /// <summary>
    /// Companion to the regression above, at the <c>LoadAsync</c> level rather than only
    /// <c>FindSolutionFile</c> directly: an INFERRED target — here, a directory resolved (via
    /// <c>ResolveProjectPath</c>'s directory branch) to the single <c>.csproj</c> it contains — must
    /// still propagate the ambiguity refusal when its ancestor directory holds two real <c>.sln</c>
    /// files. Only a caller-named <c>.csproj</c> <em>file</em> path degrades to a standalone load
    /// (#213); a directory the loader had to resolve to a project on the caller's behalf is exactly
    /// the case #172's Task 2 was written to guard. Deliberately uses an explicit directory argument
    /// rather than a bare name needing <c>Directory.SetCurrentDirectory</c>, which would race other
    /// tests reading the process-wide working directory concurrently.
    /// </summary>
    [Fact]
    public async Task LoadAsync_PropagatesAmbiguity_ForADirectoryTarget_EvenWithAnAmbiguousAncestorSolution()
    {
        var csprojPath = Touch(Path.Combine("Src", "MyLib", "MyLib.csproj"));
        File.WriteAllText(csprojPath, MinimalCsprojXml);
        var full = Touch("Full.sln");
        var clientOnly = Touch("ClientOnly.sln");
        var myLibDir = Path.GetDirectoryName(csprojPath)!;

        var loader = new ProjectLoader(
            A.Fake<ILogger<ProjectLoader>>(),
            new MSBuildService(A.Fake<ILogger<MSBuildService>>()));

        var ex = await Should.ThrowAsync<ArgumentException>(() => loader.LoadAsync(myLibDir));
        ex.Message.ShouldContain(full);
        ex.Message.ShouldContain(clientOnly);
    }

    /// <summary>
    /// Regression: <c>ResolveProjectPath</c>'s direct-<c>.csproj</c> branch returns the caller's
    /// argument verbatim (no <c>Path.GetFullPath</c>), so a relative <c>project</c> argument must
    /// not leak into <c>ResolvedPath</c> — the documented contract is an absolute path, which the
    /// pre-#151 <c>Solution.FilePath</c>/<c>Project.FilePath</c> fallback always got for free from
    /// MSBuildWorkspace's internal normalization, regardless of the raw string passed in.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ResolvedPath_IsAlwaysAbsolute_EvenForARelativeCsprojArgument()
    {
        var csprojPath = CreateStandaloneProject();
        var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), csprojPath);

        var loader = new ProjectLoader(
            A.Fake<ILogger<ProjectLoader>>(),
            new MSBuildService(A.Fake<ILogger<MSBuildService>>()));

        using var loaded = await loader.LoadAsync(relativePath);

        Path.IsPathRooted(loaded.ResolvedPath).ShouldBeTrue();
        loaded.ResolvedPath.ShouldBe(Path.GetFullPath(csprojPath));
    }
#pragma warning restore xUnit1051

    /// <summary>
    /// <see cref="LoadedProject.ResolvedPath"/> reports the <c>.sln</c> when the solution has a file
    /// path — the field that tells two checkouts of the same repository apart.
    /// </summary>
    [Fact]
    public void ResolvedPath_PrefersTheSolutionFile()
    {
        var slnPath = Path.Combine(_baseDir, "Acme.sln");
        using var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), filePath: slnPath));
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "Acme", "Acme",
            LanguageNames.CSharp, filePath: Path.Combine(_baseDir, "Acme.csproj")));

        using var loaded = new LoadedProject(workspace, project.Solution, project, ownsWorkspace: false);

        loaded.ResolvedPath.ShouldBe(slnPath);
    }

    /// <summary>
    /// Pins the resolution contract behind the worktree bug: a worktree nested inside its own main
    /// checkout is structurally unreachable from that checkout (level 0 wins, and only immediate
    /// subdirectories are ever scanned), so an omitted <c>project</c> answers from the main
    /// checkout. That is by design — the defect was that nothing told the caller. The escape hatch,
    /// an explicit absolute path, must keep overriding the working-directory anchor.
    /// </summary>
    [Fact]
    public void AutoDiscovery_ResolvesTheNearestCheckout_AndAnExplicitWorktreePathBeatsTheCwd()
    {
        // A worktree nested inside its own main checkout, the layout Claude Code creates.
        var mainSln = Touch("Main.sln");

        var worktreeDir = Path.Combine(_baseDir, ".claude", "worktrees", "wt");
        Directory.CreateDirectory(worktreeDir);
        var worktreeSln = Touch(Path.Combine(".claude", "worktrees", "wt", "Main.sln"));

        // From the main checkout, level 0 wins immediately — the worktree is three levels down and
        // only immediate subdirectories are ever scanned, so it is unreachable. This is by design.
        ResolveTargetPath(null, _baseDir).ShouldBe(mainSln);

        // From inside the worktree, the worktree wins.
        ResolveTargetPath(null, worktreeDir).ShouldBe(worktreeSln);

        // The escape hatch: an explicit absolute path overrides the cwd anchor entirely.
        ResolveTargetPath(worktreeSln, _baseDir).ShouldBe(worktreeSln);
    }

    /// <summary>
    /// Skips unless this process can actually be denied by a Unix mode bit. Two ways it cannot:
    /// <see cref="File.SetUnixFileMode(string, UnixFileMode)"/> throws outright on Windows (CI runs
    /// a Windows leg), and root bypasses the mode bits entirely, so under a root container the
    /// fixture would build without denying anything and the test would fail for a reason unrelated
    /// to the code under test. <see cref="Assert.Skip"/> rather than a bare <c>return</c>, so the
    /// gap shows up in the run summary instead of being reported as a passing assertion-free test.
    /// </summary>
    private static void RequireEnforcedUnixPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("File.SetUnixFileMode is unsupported on Windows.");
        }

        if (Environment.IsPrivilegedProcess)
        {
            Assert.Skip("Running privileged: mode bits do not deny this process, so the fixture cannot deny access.");
        }
    }

    /// <summary>
    /// Creates an unreadable directory and restores its mode on dispose. The restore is structural
    /// rather than remembered: a directory left at <see cref="UnixFileMode.None"/> cannot be removed
    /// by <see cref="Dispose"/> and would strand a temp tree on the machine, so it must not depend
    /// on each test remembering a <c>finally</c>.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    private sealed class LockedDirectory(string fullPath) : IDisposable
    {
        public string FullPath { get; } = fullPath;

        public void Dispose() =>
            File.SetUnixFileMode(FullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [UnsupportedOSPlatform("windows")]
    private LockedDirectory Lock(string relativePath)
    {
        var fullPath = Path.Combine(_baseDir, relativePath);
        Directory.CreateDirectory(fullPath);
        File.SetUnixFileMode(fullPath, UnixFileMode.None);
        return new LockedDirectory(fullPath);
    }

    /// <summary>
    /// A bare project name falls through to the recursive sweep, and that sweep must not be
    /// derailed by a directory it happens to have no permission to read. The unreadable sibling
    /// here has nothing to do with the request; aborting the whole lookup over it was the defect.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void RecursiveSweep_SkipsAnUnreadableDirectory_AndStillFindsTheProject()
    {
        RequireEnforcedUnixPermissions();

        var csproj = Touch(Path.Combine("Readable", "Acme.csproj"));
        using var _ = Lock("Locked");

        ResolveTargetPath("Acme", _baseDir).ShouldBe(csproj);
    }

    /// <summary>
    /// The deliberate other half of the asymmetry: when the caller <em>names</em> the directory,
    /// "I could not read it" is the answer they need. Ignoring the permission failure here would
    /// degrade it into <see cref="FileNotFoundException"/> — "Project not found" — sending them off
    /// to look for a missing file instead of fixing a mode bit. So this branch keeps throwing, and
    /// <c>ToolExecutionHelper.Classify</c> is what turns the throw into a message-bearing
    /// AnalysisError rather than a scrubbed InternalError.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AnExplicitlyNamedUnreadableDirectory_StillRaisesThePermissionFailure()
    {
        RequireEnforcedUnixPermissions();

        using var locked = Lock("Locked");

        // Directory.Exists reports true for a mode-000 directory on Unix, so the caller-named branch
        // is entered and then throws — which is exactly the behavior being pinned. See the caveat on
        // ProjectLoader.ResolveProjectPath: Windows ACL-denies and answers false instead, which is
        // one of the reasons this test is Unix-only.
        Should.Throw<UnauthorizedAccessException>(() => ResolveTargetPath(locked.FullPath, _baseDir));
    }

    /// <summary>
    /// Regression guard for the enumeration-options swap: the parameterless
    /// <see cref="EnumerationOptions"/> ctor defaults <c>AttributesToSkip</c> to
    /// <c>Hidden | System</c>, and .NET infers Hidden from a leading dot on Unix — so adopting it
    /// without pinning that property back to <c>0</c> silently hides every project under a
    /// dot-directory. <c>.claude/worktrees/&lt;name&gt;/</c> is exactly such a layout, and exactly
    /// the one Claude Code creates, so this is not a hypothetical corner. Deliberately NOT
    /// Unix-only: the bug it guards was platform-split, visible only on Unix, which is precisely
    /// what makes a cross-platform assertion worth having.
    /// </summary>
    [Fact]
    public void RecursiveSweep_FindsAProjectNestedUnderADotDirectory()
    {
        var csproj = Touch(Path.Combine(".claude", "worktrees", "wt", "Acme.csproj"));

        ResolveTargetPath("Acme", _baseDir).ShouldBe(csproj);
    }

    /// <summary>
    /// The pin that keeps dot-<em>directories</em> discoverable (<c>AttributesToSkip = 0</c>, the
    /// test above) un-hides dot-prefixed <em>files</em> just the same. macOS writes an AppleDouble
    /// shadow (<c>._App.sln</c>) beside a file whenever a tree passes through a filesystem without
    /// native extended attributes — exFAT, SMB, some zip tools — and #158 turned that shadow into a
    /// second "solution": a repository that resolved cleanly became ambiguous. A shadow is never a
    /// candidate, so the real file resolves alone. Deliberately cross-platform: on Windows nothing
    /// ever hid the shadow by attribute, so the name is the only thing there is to filter on.
    /// </summary>
    [Fact]
    public void AutoDiscover_IgnoresAnAppleDoubleShadow_BesideTheRealSolution()
    {
        var sln = Touch("App.sln");
        Touch("._App.sln");

        ResolveTargetPath(null, _baseDir).ShouldBe(sln);
    }

    /// <summary>The same shadow beside a project, on auto-discovery's <c>.csproj</c> fallback.</summary>
    [Fact]
    public void AutoDiscover_IgnoresAnAppleDoubleShadow_BesideTheRealProject()
    {
        var csproj = Touch("App.csproj");
        Touch("._App.csproj");

        ResolveTargetPath(null, _baseDir).ShouldBe(csproj);
    }

    /// <summary>
    /// A shadow with no real file beside it is not a candidate either: the level reads as empty
    /// and discovery reports nothing found, rather than handing MSBuild a resource fork to open.
    /// </summary>
    [Fact]
    public void AutoDiscover_AnAppleDoubleShadowAlone_IsNothingFound()
    {
        Touch("._App.sln");

        var ex = Should.Throw<ArgumentException>(() => ResolveTargetPath(null, _baseDir));
        ex.Message.ShouldContain("Could not auto-discover");
    }

    /// <summary>
    /// Shadows are filtered <em>before</em> the ambiguity check, so a genuinely ambiguous level
    /// lists only the real candidates — the message is what the caller acts on.
    /// </summary>
    [Fact]
    public void AutoDiscover_AmbiguityMessage_ListsRealCandidatesOnly_NeverShadows()
    {
        var a = Touch("A.sln");
        var b = Touch("B.sln");
        Touch("._A.sln");

        var ex = Should.Throw<ArgumentException>(() => ResolveTargetPath(null, _baseDir));
        ex.Message.ShouldContain(a);
        ex.Message.ShouldContain(b);
        ex.Message.ShouldNotContain("._A.sln");
    }

    /// <summary>
    /// The bare-name sweep is filtered the same way: a shadow never resolves, even when the name
    /// asked for is spelled to match it exactly.
    /// </summary>
    [Fact]
    public void RecursiveSweep_NeverResolvesToAnAppleDoubleShadow()
    {
        Touch(Path.Combine("Src", "._Acme.csproj"));

        Should.Throw<FileNotFoundException>(() => ResolveTargetPath("._Acme", _baseDir));
    }

    /// <summary>
    /// Auto-discovery's final level — the base directory's immediate subdirectories — must not
    /// abort over one it cannot read. An unreadable sibling here has nothing to do with the
    /// request; a readable subdirectory holding the only <c>.sln</c> must still resolve.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AutoDiscover_SkipsAnUnreadableSubdirectory_AndStillFindsTheSolution()
    {
        RequireEnforcedUnixPermissions();

        var sln = Touch(Path.Combine("Readable", "Acme.sln"));
        using var _ = Lock("Locked");

        ResolveTargetPath(null, _baseDir).ShouldBe(sln);
    }

    /// <summary>
    /// The base directory itself is the edge case named in the spec: <see cref="Directory.Exists"/>
    /// reports <c>true</c> for a mode-000 directory on Unix, so the walk proceeds — and both
    /// enumerations rooted there must now return empty rather than throw, leaving the ordinary
    /// "nothing found" <see cref="ArgumentException"/> as the outcome, not an escaped
    /// <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AutoDiscover_UnreadableBaseDirectory_YieldsNothingFound_NotUnauthorizedAccess()
    {
        RequireEnforcedUnixPermissions();

        File.SetUnixFileMode(_baseDir, UnixFileMode.None);
        using var _ = new LockedDirectory(_baseDir);

        Should.Throw<ArgumentException>(() => ResolveTargetPath(null, _baseDir));
    }

    /// <summary>Invokes the private static <c>FindSolutionFile</c> that walks up from a resolved path to its containing solution.</summary>
    private static string? FindSolutionFile(string startPath)
    {
        var method = typeof(ProjectLoader).GetMethod(
            "FindSolutionFile", BindingFlags.NonPublic | BindingFlags.Static)!;

        try
        {
            return (string?)method.Invoke(null, [startPath]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    /// <summary>
    /// The ancestor walk climbs past a rung it cannot read instead of aborting there. The
    /// unreadable directory sits between the starting <c>.csproj</c> and the <c>.sln</c> two levels
    /// up; the walk must skip it (empty result at that rung, not a thrown exception) and keep
    /// climbing via <see cref="Directory.GetParent(string)"/>, which needs no read permission on
    /// the child it is leaving.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void FindSolutionFile_SkipsAnUnreadableIntermediateDirectory_AndFindsTheGrandparentSolution()
    {
        RequireEnforcedUnixPermissions();

        var sln = Touch(Path.Combine("GrandParent", "GrandParent.sln"));
        var csproj = Touch(Path.Combine("GrandParent", "Intermediate", "Nested.csproj"));
        var intermediateDir = Path.GetDirectoryName(csproj)!;
        File.SetUnixFileMode(intermediateDir, UnixFileMode.None);
        using var _ = new LockedDirectory(intermediateDir);

        FindSolutionFile(csproj).ShouldBe(sln);
    }

    /// <summary>
    /// The ancestor walk reads the same directories as auto-discovery and is filtered the same
    /// way: an AppleDouble shadow beside the real solution is never the file handed to
    /// <c>OpenSolutionAsync</c>. Dot-prefixed names often list first, so the shadow was exactly
    /// what <c>slnFiles[0]</c> tended to return — a failure about a resource fork, on a call that
    /// never asked about one.
    /// </summary>
    [Fact]
    public void FindSolutionFile_IgnoresAnAppleDoubleShadow_BesideTheRealSolution()
    {
        var sln = Touch("App.sln");
        Touch("._App.sln");
        var csproj = Touch(Path.Combine("Src", "App.csproj"));

        FindSolutionFile(csproj).ShouldBe(sln);
    }

    /// <summary>
    /// Two real solutions on one rung is the ambiguity auto-discovery already refuses; the walk
    /// refuses it the same way instead of silently taking whichever the OS listed first. The
    /// message names both, so the caller can pass the one they meant.
    /// </summary>
    [Fact]
    public void FindSolutionFile_RefusesAnAmbiguousDirectory_InsteadOfGuessing()
    {
        var a = Touch("A.sln");
        var b = Touch("B.sln");
        var csproj = Touch(Path.Combine("Src", "App.csproj"));

        var ex = Should.Throw<ArgumentException>(() => FindSolutionFile(csproj));
        ex.Message.ShouldContain(a);
        ex.Message.ShouldContain(b);
    }

    /// <summary>Falls back to the primary project's <c>.csproj</c> when no <c>.sln</c> was loaded.</summary>
    [Fact]
    public void ResolvedPath_FallsBackToTheProjectFile_WhenTheSolutionHasNoPath()
    {
        var csprojPath = Path.Combine(_baseDir, "Acme.csproj");
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "Acme", "Acme",
            LanguageNames.CSharp, filePath: csprojPath));

        using var loaded = new LoadedProject(workspace, project.Solution, project, ownsWorkspace: false);

        loaded.ResolvedPath.ShouldBe(csprojPath);
    }

    /// <summary>
    /// An explicit resolved path — the file the loader actually opened — wins over both
    /// <c>Solution.FilePath</c> and <c>Project.FilePath</c>, since only the caller that took the
    /// branch (the loader) knows which one really answered.
    /// </summary>
    [Fact]
    public void LoadedProject_ReportsTheExplicitResolvedPath_WhenSupplied()
    {
        var slnPath = Path.Combine(_baseDir, "Repo.sln");
        var csprojPath = Path.Combine(_baseDir, "Scratch", "Scratch.csproj");
        using var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(
            SolutionId.CreateNewId(), VersionStamp.Create(), filePath: slnPath));
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "Scratch", "Scratch",
            LanguageNames.CSharp, filePath: csprojPath));

        using var loaded = new LoadedProject(
            workspace, project.Solution, project, ownsWorkspace: false, resolvedPath: csprojPath);

        loaded.ResolvedPath.ShouldBe(csprojPath);
    }

    /// <summary>
    /// With no explicit resolved path and no file path anywhere in the loaded solution/project
    /// (a fully in-memory handle), <see cref="LoadedProject.ResolvedPath"/> falls back to empty —
    /// unchanged behavior for tests that construct handles this way.
    /// </summary>
    [Fact]
    public void LoadedProject_ResolvedPath_IsEmpty_ForPathlessInMemorySolution()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "InMemory", "InMemory", LanguageNames.CSharp));

        using var loaded = new LoadedProject(workspace, project.Solution, project, ownsWorkspace: false);

        loaded.ResolvedPath.ShouldBe(string.Empty);
    }
}
