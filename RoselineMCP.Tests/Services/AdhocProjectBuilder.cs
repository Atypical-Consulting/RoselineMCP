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
    /// <summary>Creates a workspace + project containing <paramref name="files"/> (name → C# source).</summary>
    public static (AdhocWorkspace Workspace, Project Project) Create(
        string projectName,
        IEnumerable<(string Name, string Code)> files,
        string? baseDirectory = null)
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
                documentId, name, SourceText.From(code),
                filePath: Path.Combine(baseDirectory, name));
        }

        var project = solution.GetProject(projectId)!;
        return (workspace, project);
    }

    /// <summary>Creates a fake <see cref="IProjectLoader"/> that always returns the given project.</summary>
    public static IProjectLoader FakeLoaderFor(AdhocWorkspace workspace, Project project)
    {
        var loader = A.Fake<IProjectLoader>();
        A.CallTo(() => loader.LoadAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(new LoadedProject(workspace, project.Solution, project)));
        return loader;
    }
}
