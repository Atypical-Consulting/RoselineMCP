using Microsoft.Extensions.Logging;
using FakeItEasy;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>Tests for <see cref="SolutionFileBuilder"/>, the shared <c>.sln</c> fixture writer.</summary>
public class SolutionFileBuilderTests : IDisposable
{
    private readonly string _testDirectory;

    public SolutionFileBuilderTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"SolutionFileBuilder_{Guid.NewGuid():N}");
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
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void Write_With_ProjectNames_Lists_Each_With_A_Distinct_Guid_And_Config_Entry()
    {
        var slnPath = Path.Combine(_testDirectory, "App.sln");

        SolutionFileBuilder.Write(slnPath, "App", "Lib");

        var content = File.ReadAllText(slnPath);

        content.ShouldContain("Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"App\\App.csproj\", \"{11111111-1111-1111-1111-111111111111}\"");
        content.ShouldContain("Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Lib\", \"Lib\\Lib.csproj\", \"{11111112-1111-1111-1111-111111111111}\"");

        content.ShouldContain("{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
        content.ShouldContain("{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU");
        content.ShouldContain("{11111112-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
        content.ShouldContain("{11111112-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU");
    }

    [Fact]
    public void Write_With_Explicit_Paths_Normalizes_Forward_Slashes_To_Backslashes()
    {
        var slnPath = Path.Combine(_testDirectory, "Repo.sln");

        SolutionFileBuilder.Write(slnPath, ("Main", "Main/Main.csproj"));

        var content = File.ReadAllText(slnPath);

        content.ShouldContain("\"Main\\Main.csproj\"");
        content.ShouldNotContain("Main/Main.csproj");
    }

    [Fact]
#pragma warning disable xUnit1051 // TestContext.Current not needed here
    public async Task Write_Produces_A_Solution_MSBuildWorkspace_Can_Actually_Load()
    {
        foreach (var name in new[] { "App", "Lib" })
        {
            var projectDir = Path.Combine(_testDirectory, name);
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, $"{name}.csproj"), MinimalCsprojXml);
            File.WriteAllText(Path.Combine(projectDir, "Widget.cs"), $"namespace {name} {{ public class Widget {{ }} }}");
        }

        var slnPath = Path.Combine(_testDirectory, "App.sln");
        SolutionFileBuilder.Write(slnPath, "App", "Lib");

        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        using var workspace = msBuildService.CreateWorkspace();

        var solution = await workspace.OpenSolutionAsync(slnPath);

        solution.Projects.Count().ShouldBe(2);
        solution.Projects.ShouldContain(p => p.Name == "App");
        solution.Projects.ShouldContain(p => p.Name == "Lib");
    }
#pragma warning restore xUnit1051
}
