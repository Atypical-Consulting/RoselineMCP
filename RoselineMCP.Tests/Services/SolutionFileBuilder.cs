namespace RoselineMCP.Tests.Services;

/// <summary>
/// Writes a minimal, MSBuildWorkspace-loadable Visual Studio <c>.sln</c> file to disk, for the test
/// suites that need a real solution file (as opposed to <see cref="AdhocProjectBuilder"/>'s in-memory
/// <see cref="Microsoft.CodeAnalysis.AdhocWorkspace"/> solutions). Deterministic GUIDs and a single
/// <c>Debug|Any CPU</c> configuration — no test needs more than that.
/// </summary>
internal static class SolutionFileBuilder
{
    private const string ProjectTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    /// <summary>
    /// Writes <paramref name="slnPath"/> listing each of <paramref name="projectNames"/> at its
    /// conventional <c>&lt;Name&gt;\&lt;Name&gt;.csproj</c> path (relative to the .sln's directory).
    /// Returns <paramref name="slnPath"/>.
    /// </summary>
    public static string Write(string slnPath, params string[] projectNames) =>
        Write(slnPath, projectNames.Select(n => (n, $"{n}\\{n}.csproj")).ToArray());

    /// <summary>
    /// Same as <see cref="Write(string, string[])"/>, but with an explicit relative <c>.csproj</c>
    /// path per project — for layouts where a project is not listed at its conventional path, or
    /// where a suite needs a project deliberately absent from the solution altogether (by not
    /// including it here). <paramref name="projects"/>' relative paths may use either directory
    /// separator; they are normalized to backslashes as the solution file format expects (MSBuild
    /// normalizes those back on Unix). Returns <paramref name="slnPath"/>.
    /// </summary>
    public static string Write(string slnPath, params (string Name, string RelativeCsprojPath)[] projects)
    {
        if (projects.Length > 9)
        {
            // The GUID scheme below packs the 1-based index into a single hex digit after a fixed
            // 7-digit "1111111" prefix; a 10th project would overflow that digit and emit a
            // malformed (9-hex-digit) GUID segment instead of failing loudly.
            throw new ArgumentException(
                $"{nameof(SolutionFileBuilder)} supports at most 9 projects per solution (deterministic single-digit GUID scheme); got {projects.Length}.",
                nameof(projects));
        }

        var entries = projects
            .Select((p, i) => (
                Name: p.Name,
                RelativePath: p.RelativeCsprojPath.Replace('/', '\\'),
                Guid: $"{{1111111{i + 1}-1111-1111-1111-111111111111}}"))
            .ToList();

        var projectBlocks = string.Join("\n", entries.Select(p =>
            $"Project(\"{ProjectTypeGuid}\") = \"{p.Name}\", \"{p.RelativePath}\", \"{p.Guid}\"\nEndProject"));
        var configBlocks = string.Join("\n", entries.Select(p =>
            $"\t\t{p.Guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\n\t\t{p.Guid}.Debug|Any CPU.Build.0 = Debug|Any CPU"));

        var directory = Path.GetDirectoryName(slnPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(slnPath,
            $"""
             Microsoft Visual Studio Solution File, Format Version 12.00
             # Visual Studio Version 17
             VisualStudioVersion = 17.0.31903.59
             MinimumVisualStudioVersion = 10.0.40219.1
             {projectBlocks}
             Global
             	GlobalSection(SolutionConfigurationPlatforms) = preSolution
             		Debug|Any CPU = Debug|Any CPU
             	EndGlobalSection
             	GlobalSection(ProjectConfigurationPlatforms) = postSolution
             {configBlocks}
             	EndGlobalSection
             EndGlobal
             """);

        return slnPath;
    }
}
