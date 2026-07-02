using System.Text;

namespace RoselineMCP.Benchmarks.Fixtures;

/// <summary>
/// Generates small, throwaway multi-project C# solutions on disk for benchmarking the
/// <c>SolutionAnalyzerService</c> / <c>CodeFixService</c> service layer against something more
/// representative than a single file, without needing a checked-in "realistic" solution.
/// </summary>
/// <remarks>
/// Every generated project is a plain SDK-style class library with no <c>PackageReference</c>s,
/// so <c>MSBuildWorkspace</c> can design-time-build it without a prior <c>dotnet restore</c> —
/// the same trick <c>CodeFixServiceIntegrationTests</c> uses. Each generated file contains one
/// intentionally unused local variable (<c>CS0219</c>), which is both a real diagnostic for
/// <c>AnalyzeSolution</c> to find and a real fix for <c>ApplyFixes</c> (previewOnly) to compute,
/// without requiring any third-party analyzer packages to be restored.
/// </remarks>
public static class BenchmarkSolutionFixture
{
    /// <summary>
    /// A generated fixture: the path to its .sln file, plus the path to the first project's
    /// .csproj file (used directly by the ApplyFixes benchmarks, which target a single project).
    /// </summary>
    public sealed record FixtureSolution(string SolutionPath, string FirstProjectPath);

    /// <summary>
    /// Creates <paramref name="projectCount"/> class library projects, each with
    /// <paramref name="filesPerProject"/> source files, wired together into a single .sln under
    /// a fresh subdirectory of <paramref name="rootDirectory"/>.
    /// </summary>
    public static FixtureSolution Create(string rootDirectory, string name, int projectCount, int filesPerProject)
    {
        var solutionDir = Path.Combine(rootDirectory, name);
        Directory.CreateDirectory(solutionDir);

        var projects = new List<(string Name, Guid Guid, string CsprojPath)>();

        for (var p = 0; p < projectCount; p++)
        {
            var projectName = $"{name}Project{p:D2}";
            var projectDir = Path.Combine(solutionDir, projectName);
            Directory.CreateDirectory(projectDir);

            var csprojPath = Path.Combine(projectDir, $"{projectName}.csproj");
            File.WriteAllText(csprojPath, ProjectXml);

            for (var f = 0; f < filesPerProject; f++)
            {
                var filePath = Path.Combine(projectDir, $"Class{f:D3}.cs");
                File.WriteAllText(filePath, BuildSourceFile(projectName, f));
            }

            projects.Add((projectName, Guid.NewGuid(), csprojPath));
        }

        var solutionPath = Path.Combine(solutionDir, $"{name}.sln");
        File.WriteAllText(solutionPath, BuildSolutionFile(projects));

        return new FixtureSolution(solutionPath, projects[0].CsprojPath);
    }

    private const string ProjectXml =
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
    /// One CS0219 ("unused local") plus a couple of trivial members, so each file contributes a
    /// consistent, cheaply-computed diagnostic without pulling in any analyzer packages.
    /// </summary>
    private static string BuildSourceFile(string projectName, int index) =>
        $$"""
        using System;

        namespace RoselineMCP.Benchmarks.Generated.{{projectName}};

        public class Class{{index:D3}}
        {
            public int Value { get; set; } = {{index}};

            public void Report()
            {
                int unused = {{index}};
                Console.WriteLine($"Class{{index:D3}} = {Value}");
            }

            public int DoubleValue() => Value * 2;
        }
        """;

    private static string BuildSolutionFile(List<(string Name, Guid Guid, string CsprojPath)> projects)
    {
        const string projectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("Microsoft Visual Studio Solution File, Format Version 12.00");

        foreach (var (name, guid, csprojPath) in projects)
        {
            var relativePath = Path.GetFileName(Path.GetDirectoryName(csprojPath)!) + Path.DirectorySeparatorChar + Path.GetFileName(csprojPath);
            var guidStr = "{" + guid.ToString("D").ToUpperInvariant() + "}";
            sb.AppendLine($"Project(\"{projectTypeGuid}\") = \"{name}\", \"{relativePath}\", \"{guidStr}\"");
            sb.AppendLine("EndProject");
        }

        sb.AppendLine("Global");
        sb.AppendLine("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution");
        sb.AppendLine("\t\tDebug|Any CPU = Debug|Any CPU");
        sb.AppendLine("\t\tRelease|Any CPU = Release|Any CPU");
        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution");

        foreach (var (_, guid, _) in projects)
        {
            var guidStr = "{" + guid.ToString("D").ToUpperInvariant() + "}";
            sb.AppendLine($"\t\t{guidStr}.Debug|Any CPU.ActiveCfg = Debug|Any CPU");
            sb.AppendLine($"\t\t{guidStr}.Debug|Any CPU.Build.0 = Debug|Any CPU");
            sb.AppendLine($"\t\t{guidStr}.Release|Any CPU.ActiveCfg = Release|Any CPU");
            sb.AppendLine($"\t\t{guidStr}.Release|Any CPU.Build.0 = Release|Any CPU");
        }

        sb.AppendLine("\tEndGlobalSection");
        sb.AppendLine("EndGlobal");

        return sb.ToString();
    }
}
