using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using Microsoft.CodeAnalysis;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using Shouldly;

namespace RoselineMCP.Tests.Services;

public class CodeFixServiceTests
{
    private readonly ILogger<CodeFixService> _logger;
    private readonly ISolutionAnalyzerService _analyzerService;
    private readonly ICodeFixProviderFactory _codeFixProviderFactory;
    private readonly IDiffService _diffService;
    private readonly IProjectLoader _projectLoader;
    private readonly IVerificationService _verificationService;
    private readonly CodeFixService _sut;

    public CodeFixServiceTests()
    {
        _logger = A.Fake<ILogger<CodeFixService>>();
        _analyzerService = A.Fake<ISolutionAnalyzerService>();
        _codeFixProviderFactory = A.Fake<ICodeFixProviderFactory>();
        _diffService = A.Fake<IDiffService>();
        _projectLoader = A.Fake<IProjectLoader>();
        _verificationService = new VerificationService(
            A.Fake<ILogger<VerificationService>>(), DiagnosticComputationService.CompilerOnly);
        _sut = new CodeFixService(
            _logger, _analyzerService, _codeFixProviderFactory, _diffService, _projectLoader, _verificationService);
    }

    public class ApplyFixesAsyncTests : CodeFixServiceTests
    {
        /// <summary>
        /// A failure of the operation itself (here: the project doesn't exist, reported by the
        /// shared <see cref="IProjectLoader"/>) must propagate as an exception so the MCP tool
        /// boundary (ApplyFixesTool) classifies it into the documented error envelope
        /// (FileNotFoundException → NotFoundError). It must NOT be folded into a normal-looking
        /// response with an "Error: ..." note — that made the tool report ok: true for an
        /// operation that actually failed.
        /// </summary>
        [Fact]
        public async Task Should_Throw_FileNotFoundException_When_Project_Not_Found()
        {
            // Arrange
            var nonExistentProject = "/nonexistent/project.csproj";
            var ids = new List<string> { "CS0168" };
            A.CallTo(() => _projectLoader.LoadAsync(nonExistentProject, A<CancellationToken>._))
                .Throws(new FileNotFoundException($"Project not found: {nonExistentProject}"));

            // Act & Assert
            await Should.ThrowAsync<FileNotFoundException>(
                () => _sut.ApplyFixesAsync(nonExistentProject, ids));
        }

        /// <summary>
        /// Proves that a pre-cancelled token is actually honored: cancellation must propagate as
        /// an <see cref="OperationCanceledException"/> so that ApplyFixesTool's dedicated
        /// cancellation handling fires and reports a Cancelled/Timeout error instead of a
        /// fake-success response.
        /// </summary>
        [Fact]
        public async Task Should_Throw_OperationCanceledException_When_Token_Already_Cancelled()
        {
            // Arrange
            var project = "TestProject";
            var ids = new List<string> { "CS0168" };
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert — cancellation must propagate, not be swallowed into a response.
            await Should.ThrowAsync<OperationCanceledException>(
                () => _sut.ApplyFixesAsync(project, ids, previewOnly: true, cancellationToken: cts.Token));
        }
    }

    // NOTE: the former private ResolveProjectPath copy (and its TestableCodeFixService wrapper)
    // was deleted — project resolution now goes through the shared IProjectLoader, covered by
    // ProjectLoaderTests.

    public class LoadCodeFixProvidersTests : CodeFixServiceTests
    {
        [Fact]
        public void Should_Load_Providers_On_Construction()
        {
            // The constructor already calls LoadCodeFixProviders
            // Just verify the service was created successfully
            _sut.ShouldNotBeNull();
        }

        [Fact]
        public void Should_Handle_Missing_Assemblies_Gracefully()
        {
            // The service should not throw even if some assemblies are not found
            var service = new CodeFixService(
                _logger, _analyzerService, _codeFixProviderFactory, _diffService, _projectLoader, _verificationService);
            service.ShouldNotBeNull();
        }
    }

    /// <summary>
    /// The skipped-projects note (#156) on shapes an MSBuild fixture cannot build without a restore
    /// — a multi-targeted sibling — and on the one distinction the loader has to carry for it:
    /// what the caller <em>named</em> (<see cref="LoadedProject.TargetPath"/>) versus what answered.
    /// </summary>
    public class ProjectScopeNoteTests : CodeFixServiceTests
    {
        private static readonly string BaseDirectory =
            Path.Combine(Path.GetTempPath(), "roseline-tests", Guid.NewGuid().ToString("n"));

        /// <summary>
        /// <c>App.sln</c> holding <c>App</c> (the anchor) and <c>Lib</c> multi-targeting two TFMs — which
        /// Roslyn loads as two projects, <c>Lib(net8.0)</c> and <c>Lib(net10.0)</c>, over one <c>.csproj</c>.
        /// </summary>
        private static (AdhocWorkspace Workspace, Project Anchor, string SolutionPath) CreateSolutionWithMultiTargetedSibling()
        {
            var workspace = new AdhocWorkspace();
            var solutionPath = Path.Combine(BaseDirectory, "App.sln");
            var solution = workspace.AddSolution(SolutionInfo.Create(
                SolutionId.CreateNewId(), VersionStamp.Create(), filePath: solutionPath));

            var appId = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(
                appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp,
                filePath: Path.Combine(BaseDirectory, "App", "App.csproj")));

            foreach (var tfm in new[] { "net8.0", "net10.0" })
            {
                solution = solution.AddProject(ProjectInfo.Create(
                    ProjectId.CreateNewId(), VersionStamp.Create(), $"Lib({tfm})", "Lib", LanguageNames.CSharp,
                    filePath: Path.Combine(BaseDirectory, "Lib", "Lib.csproj")));
            }

            return (workspace, solution.GetProject(appId)!, solutionPath);
        }

        private void LoaderReturns(AdhocWorkspace workspace, Project anchor, string targetPath) =>
            A.CallTo(() => _projectLoader.LoadAsync(A<string>._, A<CancellationToken>._))
                .ReturnsLazily(() => Task.FromResult(new LoadedProject(workspace, anchor.Solution, anchor, targetPath: targetPath)));

        [Fact]
        public async Task A_Multi_Targeted_Sibling_Is_One_Skipped_Project_Not_One_Per_TFM()
        {
            var (workspace, anchor, solutionPath) = CreateSolutionWithMultiTargetedSibling();
            using var _ = workspace;
            LoaderReturns(workspace, anchor, targetPath: solutionPath);

            var result = await _sut.ApplyFixesAsync(solutionPath, ["CS0219"], previewOnly: true);

            var note = result.Notes.Where(n => n.Contains("not analyzed or fixed")).ShouldHaveSingleItem();
            note.ShouldContain("1 other project in 'App.sln' (Lib)");
            note.ShouldNotContain("net8.0");
        }

        /// <summary>
        /// The caller named <c>App.csproj</c>; the loader opened its ancestor <c>App.sln</c> to answer.
        /// Telling that caller "the other projects were skipped — pass a .csproj" would be telling
        /// them to do what they just did, so the note keys on what was named, not on what answered.
        /// </summary>
        [Fact]
        public async Task A_Named_Csproj_Gets_No_Skipped_Project_Note_Even_When_Its_Solution_Answered()
        {
            var (workspace, anchor, _) = CreateSolutionWithMultiTargetedSibling();
            using var __ = workspace;
            LoaderReturns(workspace, anchor, targetPath: anchor.FilePath!);

            var result = await _sut.ApplyFixesAsync(anchor.FilePath, ["CS0219"], previewOnly: true);

            result.ResolvedPath.ShouldEndWith("App.sln");
            result.Notes.ShouldNotContain(n => n.Contains("not analyzed or fixed"));
        }
    }
}
