using System.Reflection;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="ProjectLoader"/>'s <b>file-anchored</b> resolution — "which solution owns
/// this file?", the question the compile guard asks after a write.
/// </summary>
/// <remarks>
/// Anchoring on the edited file rather than on the server's working directory is the point of this
/// path. The server's cwd is fixed at spawn and is not the agent's; they diverge whenever work
/// happens in a git worktree, which is exactly the divergence
/// <see cref="RoselineMCP.Models.VerificationVerdict.ResolvedPath"/> exists to expose. A guard that
/// resolved by cwd would faithfully report on the wrong checkout.
///
/// The pure resolution step is exercised directly against a hermetic temp tree (no MSBuild); three
/// integration tests then prove the loaded handle really names the solution, the lone project, or
/// nothing.
/// </remarks>
public class ProjectLoaderFileAnchorTests : IDisposable
{
    private readonly string _root;
    private readonly string _baseDir;

    public ProjectLoaderFileAnchorTests()
    {
        // Nest a few levels deep under a fresh root so the upward walk stays inside this test's own
        // (empty) tree and can never pick up a stray .sln/.csproj from the machine.
        _root = Path.Combine(Path.GetTempPath(), $"RoselineFileAnchor_{Guid.NewGuid():N}");
        _baseDir = Path.Combine(_root, "a", "b", "work");
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignored */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>Invokes the internal static <c>ResolveProjectForFile</c>, unwrapping reflection's wrapper.</summary>
    private static string? ResolveProjectForFile(string? absoluteFilePath)
    {
        var method = typeof(ProjectLoader).GetMethod(
            "ResolveProjectForFile", BindingFlags.NonPublic | BindingFlags.Static)!;

        try
        {
            return (string?)method.Invoke(null, [absoluteFilePath]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private string Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_baseDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private static ProjectLoader CreateLoader() =>
        new(A.Fake<ILogger<ProjectLoader>>(), new MSBuildService(A.Fake<ILogger<MSBuildService>>()));

    // ---- pure resolution ---------------------------------------------------------------------

    [Fact]
    public void Resolves_The_Csproj_Sitting_Beside_The_File()
    {
        var csproj = Write("Lib.csproj", MinimalCsprojXml);
        var source = Write("Widget.cs", "public class Widget { }");

        ResolveProjectForFile(source).ShouldBe(csproj);
    }

    [Fact]
    public void Walks_Up_To_The_Nearest_Csproj_Above_The_File()
    {
        var csproj = Write("Lib.csproj", MinimalCsprojXml);
        var source = Write(Path.Combine("Services", "Deep", "Widget.cs"), "public class Widget { }");

        ResolveProjectForFile(source).ShouldBe(csproj);
    }

    [Fact]
    public void Prefers_The_Nearest_Csproj_When_Projects_Are_Nested()
    {
        Write("Outer.csproj", MinimalCsprojXml);
        var inner = Write(Path.Combine("Inner", "Inner.csproj"), MinimalCsprojXml);
        var source = Write(Path.Combine("Inner", "Widget.cs"), "public class Widget { }");

        ResolveProjectForFile(source).ShouldBe(inner);
    }

    [Fact]
    public void Returns_Null_For_A_File_Under_No_Project_At_All()
    {
        var orphan = Write("notes.cs", "// nobody's project");

        // Not an exception: the guard fires on every write, including writes in directories that
        // have nothing to do with any solution. "Nothing to verify" must be an ordinary answer.
        ResolveProjectForFile(orphan).ShouldBeNull();
    }

    [Fact]
    public void Rejects_A_Relative_Path()
    {
        // The hook envelope's `cwd` is the AGENT's, not the server's, so a relative path could only
        // be resolved against the wrong directory. Refusing is the only honest option.
        Should.Throw<ArgumentException>(() => ResolveProjectForFile(Path.Combine("src", "Widget.cs")));
    }

    [Fact]
    public void Rejects_A_Blank_Path()
    {
        Should.Throw<ArgumentException>(() => ResolveProjectForFile("   "));
    }

    // ---- loading -----------------------------------------------------------------------------

    /// <summary>A file inside a solution resolves the <c>.sln</c>, not merely its own project.</summary>
    [Fact]
#pragma warning disable xUnit1051 // TestContext.Current not needed here
    public async Task LoadForFileAsync_Resolves_The_Containing_Solution()
    {
        var (slnPath, sourcePath) = CreateRealSolution();

        using var loaded = await CreateLoader().LoadForFileAsync(sourcePath);

        loaded.ShouldNotBeNull();
        loaded.ResolvedPath.ShouldBe(slnPath);
        loaded.Project.Name.ShouldBe("App");
    }

    /// <summary>
    /// With no <c>.sln</c> anywhere above it, the lone <c>.csproj</c> is what gets loaded — which is
    /// what makes the verdict's <c>scopeComplete</c> false downstream, because dependents outside
    /// that project cannot be seen.
    /// </summary>
    [Fact]
    public async Task LoadForFileAsync_Falls_Back_To_The_Lone_Csproj_When_No_Solution_Exists()
    {
        var csproj = Write(Path.Combine("Solo", "Solo.csproj"), MinimalCsprojXml);
        var source = Write(Path.Combine("Solo", "Widget.cs"), "namespace Solo { public class Widget { } }");

        using var loaded = await CreateLoader().LoadForFileAsync(source);

        loaded.ShouldNotBeNull();
        loaded.ResolvedPath.ShouldBe(csproj);
    }

    [Fact]
    public async Task LoadForFileAsync_Returns_Null_For_A_File_Outside_Any_Project()
    {
        var orphan = Write("notes.cs", "// nobody's project");

        var loaded = await CreateLoader().LoadForFileAsync(orphan);

        loaded.ShouldBeNull();
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
    /// build it offline) plus a hand-written <c>.sln</c> referencing it. Returns the .sln path and a
    /// source file inside the project.
    /// </summary>
    private (string SolutionPath, string SourcePath) CreateRealSolution()
    {
        var projectDir = Path.Combine(_baseDir, "App");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"), MinimalCsprojXml);

        var sourcePath = Path.Combine(projectDir, "Widget.cs");
        File.WriteAllText(sourcePath, "namespace App { public class Widget { } }");

        var slnPath = Path.Combine(_baseDir, "App.sln");
        File.WriteAllText(slnPath,
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            VisualStudioVersion = 17.0.31903.59
            MinimumVisualStudioVersion = 10.0.40219.1
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            		Release|Any CPU = Release|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
            	EndGlobalSection
            EndGlobal
            """);

        return (slnPath, sourcePath);
    }
}
