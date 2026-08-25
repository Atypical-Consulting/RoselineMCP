using System.Collections.Immutable;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// The compile gate on <c>apply_fixes</c>, exercised end-to-end against a real project on disk with
/// a real code fix. A code fix is generated code the caller never wrote, so "the fixer said it was
/// fine" is precisely the assurance worth checking rather than trusting — and unlike the other two
/// write tools, <c>apply_fixes</c> had no <c>applied</c> field at all, which made a refusal
/// indistinguishable from a success.
/// </summary>
public class CodeFixServiceVerificationTests : IDisposable
{
    private readonly string _testDirectory;

    public CodeFixServiceVerificationTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CodeFixVerify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDirectory, true); } catch { /* ignored */ }
        GC.SuppressFinalize(this);
    }

    private const string MinimalCsprojXml =
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string SourceWithUnusedLocal =
        """
        class Fixture
        {
            static void Main()
            {
                int unused = 1;
                System.Console.WriteLine("hello");
            }
        }
        """;

    private (string CsprojPath, string SourcePath) CreateProject()
    {
        var projectDir = Path.Combine(_testDirectory, "Fixture");
        Directory.CreateDirectory(projectDir);
        var csprojPath = Path.Combine(projectDir, "Fixture.csproj");
        File.WriteAllText(csprojPath, MinimalCsprojXml);
        var sourcePath = Path.Combine(projectDir, "Fixture.cs");
        File.WriteAllText(sourcePath, SourceWithUnusedLocal);
        return (csprojPath, sourcePath);
    }

    /// <summary>Answers with a fixed verdict and counts how often it was asked.</summary>
    private sealed class StubVerification(VerificationVerdict verdict) : IVerificationService
    {
        public int Calls { get; private set; }

        public Task<VerificationVerdict> VerifyAsync(
            Solution? baseline,
            Solution candidate,
            string? baseDirectory,
            int max = 20,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(verdict);
        }
    }

    /// <summary>Removes the line an unused-local diagnostic (CS0219) sits on.</summary>
    private sealed class RemoveUnusedLocalFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ["CS0219"];

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics[0];
            context.RegisterCodeFix(
                CodeAction.Create(
                    "Remove the unused local",
                    async ct =>
                    {
                        var text = await context.Document.GetTextAsync(ct);
                        var line = text.Lines.GetLineFromPosition(diagnostic.Location.SourceSpan.Start);
                        return context.Document.WithText(
                            text.Replace(TextSpan.FromBounds(line.Start, line.EndIncludingLineBreak), string.Empty));
                    },
                    equivalenceKey: "RemoveUnusedLocal"),
                diagnostic);
            return Task.CompletedTask;
        }
    }

    private static CodeFixService CreateSut(IVerificationService verification)
    {
        var factory = A.Fake<ICodeFixProviderFactory>();
        A.CallTo(() => factory.GetProviderForDiagnostic("CS0219", A<Project?>._)).Returns(new RemoveUnusedLocalFixProvider());
        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        return new CodeFixService(
            A.Fake<ILogger<CodeFixService>>(),
            A.Fake<ISolutionAnalyzerService>(),
            factory,
            new DiffService(),
            new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuildService),
            verification);
    }

    [Fact]
    public async Task Refused_Fixes_Report_Applied_False_And_Write_Nothing()
    {
        // Arrange
        var (csprojPath, sourcePath) = CreateProject();
        var verification = new StubVerification(new VerificationVerdict
        {
            Compiles = false,
            Introduced = [new DiagnosticDetail { Id = "CS0103", Severity = "error", File = "Fixture.cs", Line = 5 }]
        });
        var sut = CreateSut(verification);

        // Act — an explicit opt-in to write.
        var response = await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false);

        // Assert — without `applied`, this response would be indistinguishable from a success: it
        // still carries previewOnly=false, a patch and a fixedCount.
        response.Applied.ShouldBeFalse();
        response.PreviewOnly.ShouldBeFalse();
        response.FixedCount.ShouldBeGreaterThan(0);
        response.Patch.ShouldNotBeNullOrEmpty();
        response.Verification.ShouldNotBeNull();
        response.Verification.Introduced.ShouldNotBeNull();
        response.Notes.ShouldContain(n => n.StartsWith("Refused:", StringComparison.Ordinal));

        (await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken)).ShouldBe(SourceWithUnusedLocal);
    }

    [Fact]
    public async Task Clean_Fixes_Report_Applied_True_And_Write()
    {
        var (csprojPath, sourcePath) = CreateProject();
        var sut = CreateSut(new StubVerification(new VerificationVerdict { Compiles = true, ScopeComplete = true }));

        var response = await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false);

        response.Applied.ShouldBeTrue();
        (await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken)).ShouldNotContain("int unused");
    }

    [Fact]
    public async Task A_Preview_Is_Never_Applied_Even_When_Verification_Is_Clean()
    {
        var (csprojPath, sourcePath) = CreateProject();
        var sut = CreateSut(new StubVerification(new VerificationVerdict { Compiles = true, ScopeComplete = true }));

        var response = await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: true);

        response.Applied.ShouldBeFalse();
        response.Verification.ShouldNotBeNull();
        (await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken)).ShouldBe(SourceWithUnusedLocal);
    }

    [Fact]
    public async Task Refused_Fixes_Can_Be_Forced_With_AllowIntroducedErrors()
    {
        var (csprojPath, sourcePath) = CreateProject();
        var sut = CreateSut(new StubVerification(new VerificationVerdict
        {
            Compiles = false,
            Introduced = [new DiagnosticDetail { Id = "CS0103", Severity = "error", File = "Fixture.cs", Line = 5 }]
        }));

        var response = await sut.ApplyFixesAsync(
            csprojPath, ["CS0219"], previewOnly: false, allowIntroducedErrors: true);

        response.Applied.ShouldBeTrue();
        (await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken)).ShouldNotContain("int unused");
    }

    [Fact]
    public async Task One_Call_Verifies_Once()
    {
        // One verification per call means one baseline compilation per call. A second verify — one
        // to report and one to gate — would silently double the cost of every apply_fixes.
        var (csprojPath, _) = CreateProject();
        var verification = new StubVerification(new VerificationVerdict { Compiles = true, ScopeComplete = true });
        var sut = CreateSut(verification);

        await sut.ApplyFixesAsync(csprojPath, ["CS0219"], previewOnly: false);

        verification.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_Call_That_Changes_Nothing_Does_Not_Verify()
    {
        // Nothing to verify, so nothing to pay for: an unfixable ID must not cost a compilation.
        var (csprojPath, _) = CreateProject();
        var verification = new StubVerification(new VerificationVerdict { Compiles = true });
        var sut = CreateSut(verification);

        var response = await sut.ApplyFixesAsync(csprojPath, ["CS9999"], previewOnly: false);

        verification.Calls.ShouldBe(0);
        response.Applied.ShouldBeFalse();
        response.Verification.ShouldBeNull();
    }
}
