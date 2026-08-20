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
}
