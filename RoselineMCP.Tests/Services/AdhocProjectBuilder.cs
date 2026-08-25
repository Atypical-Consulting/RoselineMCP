using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoselineMCP.Interfaces;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Builds real in-memory Roslyn projects (via <see cref="AdhocWorkspace"/>, no MSBuild) for testing
/// the navigation and edit services against genuine compilations, and a matching fake
/// <see cref="IProjectLoader"/>. Documents are given file paths so diff/relative-path/write logic is
/// exercised; the framework reference set is included so <c>string</c>, <c>Task</c>, etc. resolve.
/// </summary>
internal static class AdhocProjectBuilder
{
    /// <summary>
    /// Creates a workspace + project containing <paramref name="files"/> (name → C# source).
    /// <paramref name="encoding"/> is attached to each document's <see cref="SourceText"/>, mirroring
    /// how MSBuildWorkspace records the on-disk encoding when it loads real files (null = in-memory
    /// text with no encoding).
    /// </summary>
    public static (AdhocWorkspace Workspace, Project Project) Create(
        string projectName,
        IEnumerable<(string Name, string Code)> files,
        string? baseDirectory = null,
        System.Text.Encoding? encoding = null)
    {
        baseDirectory ??= Path.Combine(Path.GetTempPath(), "roseline-tests", Guid.NewGuid().ToString("n"));

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            projectName,
            LanguageNames.CSharp,
            filePath: Path.Combine(baseDirectory, projectName + ".csproj"),
            metadataReferences: references,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        foreach (var (name, code) in files)
        {
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(
                documentId, name, SourceText.From(code, encoding),
                filePath: Path.Combine(baseDirectory, name));
        }

        var project = solution.GetProject(projectId)!;
        return (workspace, project);
    }

    /// <summary>
    /// Creates a workspace containing multiple sibling projects (mirroring a loaded <c>.sln</c>) and
    /// returns the workspace plus the anchor (first) project. Projects don't reference each other
    /// unless listed in <paramref name="projectReferences"/> (From → To by project name), so tests
    /// can model a sibling project the anchor cannot see through references. When
    /// <paramref name="solutionFileName"/> is given, the solution gets a <c>FilePath</c> under
    /// <paramref name="baseDirectory"/> — mirroring an MSBuild-loaded <c>.sln</c> — so a handle
    /// built over it reports that <c>.sln</c> as <c>resolvedPath</c> and tests can assert
    /// solution-root-relative output paths.
    /// </summary>
    public static (AdhocWorkspace Workspace, Project Anchor) CreateSolution(
        (string ProjectName, (string Name, string Code)[] Files)[] projects,
        (string From, string To)[]? projectReferences = null,
        string? baseDirectory = null,
        string? solutionFileName = null)
    {
        baseDirectory ??= Path.Combine(Path.GetTempPath(), "roseline-tests", Guid.NewGuid().ToString("n"));

        var workspace = new AdhocWorkspace();
        var solution = solutionFileName == null
            ? workspace.CurrentSolution
            : workspace.AddSolution(SolutionInfo.Create(
                SolutionId.CreateNewId(),
                VersionStamp.Create(),
                filePath: Path.Combine(baseDirectory, solutionFileName)));

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var projectIds = new Dictionary<string, ProjectId>(StringComparer.Ordinal);

        foreach (var (projectName, files) in projects)
        {
            var projectId = ProjectId.CreateNewId();
            projectIds[projectName] = projectId;
            var projectDirectory = Path.Combine(baseDirectory, projectName);

            var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                projectName,
                projectName,
                LanguageNames.CSharp,
                filePath: Path.Combine(projectDirectory, projectName + ".csproj"),
                metadataReferences: references,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            solution = solution.AddProject(projectInfo);

            foreach (var (name, code) in files)
            {
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId), name, SourceText.From(code),
                    filePath: Path.Combine(projectDirectory, name));
            }
        }

        foreach (var (from, to) in projectReferences ?? [])
        {
            solution = solution.AddProjectReference(projectIds[from], new ProjectReference(projectIds[to]));
        }

        var anchor = solution.GetProject(projectIds[projects[0].ProjectName])!;
        return (workspace, anchor);
    }

    /// <summary>
    /// Builds the layout from issue #151 — a solution listing only <c>Main</c>, plus a
    /// <c>Scratch</c> project that exists but is <b>not</b> listed in it. Roslyn grafts such a
    /// project onto the already-open solution, so <c>Solution.FilePath</c> keeps naming the
    /// <c>.sln</c> while the loader reports the <c>.csproj</c> as <c>resolvedPath</c> — the
    /// divergence issue #181 is about. Every source file (and the <c>.sln</c>) is also written to
    /// disk under <paramref name="baseDirectory"/>, so a reconstructed
    /// <c>Path.GetDirectoryName(resolvedPath)</c> + relative path can be checked with
    /// <see cref="File.Exists(string)"/>. The caller owns <paramref name="baseDirectory"/> and must
    /// delete it.
    /// </summary>
    public static (AdhocWorkspace Workspace, Project Scratch, string ScratchCsprojPath) CreateUnlistedProjectSolutionOnDisk(
        string baseDirectory,
        (string Name, string Code)[] mainFiles,
        (string Name, string Code)[] scratchFiles)
    {
        var projects = new[] { ("Main", mainFiles), ("Scratch", scratchFiles) };

        foreach (var (projectName, files) in projects)
        {
            var projectDirectory = Path.Combine(baseDirectory, projectName);
            Directory.CreateDirectory(projectDirectory);
            foreach (var (name, code) in files)
            {
                File.WriteAllText(Path.Combine(projectDirectory, name), code);
            }
        }

        // Only Main is listed. Nothing here parses this file — CreateSolution below adds both
        // projects to the workspace unconditionally, and the fidelity that matters comes from the
        // caller passing `resolvedPath: scratchCsproj` to FakeLoaderFor. It is written so the
        // directory on disk matches the layout the tests describe.
        File.WriteAllText(
            Path.Combine(baseDirectory, "Repo.sln"),
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Main", "Main\Main.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            """);

        // CreateSolution never applies its solution to the workspace, so reach the sibling through
        // the anchor's snapshot rather than workspace.CurrentSolution (which is still empty).
        var (workspace, anchor) = CreateSolution(projects, baseDirectory: baseDirectory, solutionFileName: "Repo.sln");
        var scratch = anchor.Solution.Projects.Single(p => p.Name == "Scratch");
        return (workspace, scratch, scratch.FilePath!);
    }

    /// <summary>
    /// Creates a fake <see cref="IProjectLoader"/> that always returns the given project.
    /// <paramref name="resolvedPath"/> mirrors what <c>ProjectLoader</c> supplies when it knows
    /// precisely which file answered — e.g. a <c>.csproj</c> opened standalone because its nearest
    /// ancestor <c>.sln</c> doesn't list it; <see langword="null"/> (the default) leaves
    /// <see cref="LoadedProject.ResolvedPath"/> on its <c>Solution.FilePath ?? Project.FilePath</c>
    /// fallback.
    /// </summary>
    public static IProjectLoader FakeLoaderFor(AdhocWorkspace workspace, Project project, string? resolvedPath = null)
    {
        var loader = A.Fake<IProjectLoader>();
        A.CallTo(() => loader.LoadAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(
                new LoadedProject(workspace, project.Solution, project, resolvedPath: resolvedPath)));
        return loader;
    }
}
