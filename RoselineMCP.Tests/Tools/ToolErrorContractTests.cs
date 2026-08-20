using FakeItEasy;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;
using RoselineMCP.Services;
using RoselineMCP.Tests.Services;
using RoselineMCP.Tools;
using Shouldly;

namespace RoselineMCP.Tests.Tools;

/// <summary>
/// Verifies the cross-cutting error contract shared by every MCP tool: raw CLR exception type
/// names must never be surfaced as the <see cref="ToolError.Type"/> field, only the documented
/// closed set of stable, machine-readable values (see ToolExecutionHelper.ToolErrorTypes). Also
/// verifies that InternalError-class responses never leak raw exception text, and that validation
/// failures include a corrective <see cref="ToolError.Hint"/>.
/// </summary>
public class ToolErrorContractTests
{
    /// <summary>
    /// The full closed set of "type" values any tool is allowed to return. Kept in sync with
    /// RoselineMCP.Tools.ToolErrorTypes (internal, so duplicated here rather than referenced
    /// across the assembly boundary).
    /// </summary>
    private static readonly HashSet<string> DocumentedErrorTypes =
    [
        "ValidationError",
        "NotFoundError",
        "AnalysisError",
        "InternalError",
        "CancelledError",
        "TimeoutError"
    ];

    public static IEnumerable<object[]> RepresentativeExceptions()
    {
        yield return [new FileNotFoundException("Solution file not found: x.sln"), "NotFoundError"];
        yield return [new DirectoryNotFoundException("Directory missing"), "NotFoundError"];
        yield return [new ArgumentException("Bad argument"), "ValidationError"];
        yield return [new InvalidOperationException("Workspace failed to load"), "AnalysisError"];
        yield return [new TimeoutException("Git clone timed out"), "AnalysisError"];
        yield return [new UnauthorizedAccessException("Access to the path '/repo/Some/File.cs' is denied."), "AnalysisError"];
        yield return [new NullReferenceException("Object reference not set"), "InternalError"];
    }

    [Theory]
    [MemberData(nameof(RepresentativeExceptions))]
    public async Task AnalyzeSolution_Classifies_Exceptions_Into_Documented_Type(Exception thrown, string expectedType)
    {
        var analyzerService = A.Fake<ISolutionAnalyzerService>();
        A.CallTo(() => analyzerService.AnalyzeSolutionAsync(
                A<string>._, A<string?>._, A<string?>._, A<string?>._, A<string?>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Throws(thrown);

        var result = await AnalyzeSolutionTool.AnalyzeSolution(analyzerService, "test.sln");

        AssertDocumentedType(result, expectedType);
    }

    [Theory]
    [MemberData(nameof(RepresentativeExceptions))]
    public async Task ListDiagnostics_Classifies_Exceptions_Into_Documented_Type(Exception thrown, string expectedType)
    {
        var analyzerService = A.Fake<ISolutionAnalyzerService>();
        A.CallTo(() => analyzerService.ListDiagnosticsAsync(
                A<string>._, A<List<string>?>._, A<List<string>?>._, A<int>._, A<CancellationToken>._))
            .Throws(thrown);

        var result = await ListDiagnosticsTool.ListDiagnostics(analyzerService, "TestProject");

        AssertDocumentedType(result, expectedType);
    }

    [Theory]
    [MemberData(nameof(RepresentativeExceptions))]
    public async Task ApplyFixes_Classifies_Exceptions_Into_Documented_Type(Exception thrown, string expectedType)
    {
        var codeFixService = A.Fake<ICodeFixService>();
        A.CallTo(() => codeFixService.ApplyFixesAsync(
                A<string>._, A<List<string>>._, A<bool>._, A<bool>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Throws(thrown);

        var result = await ApplyFixesTool.ApplyFixes(codeFixService, ["CS0168"], "TestProject");

        AssertDocumentedType(result, expectedType);
    }

    [Theory]
    [MemberData(nameof(RepresentativeExceptions))]
    public void CreatePatch_Classifies_Exceptions_Into_Documented_Type(Exception thrown, string expectedType)
    {
        var patchService = A.Fake<IPatchService>();
        A.CallTo(() => patchService.CreatePatchWithOptions(
                A<string>._, A<string>._, A<string?>._, A<int>._, A<bool>._, A<bool>._, A<CancellationToken>._))
            .Throws(thrown);

        var result = CreatePatchTool.CreatePatch(patchService, "old", "new");

        AssertDocumentedType(result, expectedType);
    }

    /// <summary>
    /// Regression test for the fake-success bug: run the REAL <see cref="CodeFixService"/> (not a
    /// fake) against a nonexistent project path, through the actual MCP tool. The service used to
    /// swallow the <see cref="FileNotFoundException"/> into an "Error: ..." note and return
    /// normally, so the tool reported ok: true for a failed operation. It must now surface as the
    /// same classified NotFoundError envelope every other tool returns.
    /// </summary>
    [Fact]
    public async Task ApplyFixes_With_Real_Service_And_Missing_Project_Returns_NotFoundError()
    {
        var codeFixService = new CodeFixService(
            A.Fake<ILogger<CodeFixService>>(),
            A.Fake<ISolutionAnalyzerService>(),
            A.Fake<ICodeFixProviderFactory>(),
            A.Fake<IDiffService>(),
            new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), A.Fake<IMSBuildService>()),
            TestVerification.New());

        var result = await ApplyFixesTool.ApplyFixes(codeFixService, ["CS0168"], "/nonexistent/x.csproj");

        AssertDocumentedType(result, "NotFoundError");
        result.Data.ShouldBeNull();
    }

    [Fact]
    public async Task InternalError_Response_Never_Leaks_Raw_Exception_Message_Or_Type_Name()
    {
        var analyzerService = A.Fake<ISolutionAnalyzerService>();
        const string sensitiveDetail = "at RoselineMCP.Internal.Secret.Method() line 42 in /Users/leak/path.cs";
        A.CallTo(() => analyzerService.AnalyzeSolutionAsync(
                A<string>._, A<string?>._, A<string?>._, A<string?>._, A<string?>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Throws(new NullReferenceException(sensitiveDetail));

        var result = await AnalyzeSolutionTool.AnalyzeSolution(analyzerService, "test.sln");

        result.Error.ShouldNotBeNull();
        result.Error.Message.ShouldNotContain(sensitiveDetail);
        result.Error.Message.ShouldNotContain("NullReferenceException");
        result.Error.Type.ShouldBe("InternalError");
    }

    /// <summary>
    /// A permission-denied failure must reach the caller classified <em>and legible</em>.
    /// <see cref="UnauthorizedAccessException"/> derives from <c>SystemException</c>, not
    /// <see cref="IOException"/>, so it used to fall through to the catch-all InternalError arm —
    /// the one arm that deliberately scrubs the message. That scrubbing is right for genuinely
    /// unexpected failures and wrong here: "Access to the path '...' is denied." is precisely the
    /// text a caller can act on, so the message assertion below is the point of this test.
    /// </summary>
    [Fact]
    public async Task Permission_Denied_Is_AnalysisError_With_Its_Message_Preserved()
    {
        const string deniedPath = "/repo/Some/File.cs";
        var analyzerService = A.Fake<ISolutionAnalyzerService>();
        A.CallTo(() => analyzerService.AnalyzeSolutionAsync(
                A<string>._, A<string?>._, A<string?>._, A<string?>._, A<string?>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Throws(new UnauthorizedAccessException($"Access to the path '{deniedPath}' is denied."));

        var result = await AnalyzeSolutionTool.AnalyzeSolution(analyzerService, "test.sln");

        AssertDocumentedType(result, "AnalysisError");
        result.Error.ShouldNotBeNull();
        result.Error.Message.ShouldContain(deniedPath);
    }

    [Fact]
    public async Task Missing_Diagnostic_Ids_Validation_Failure_Includes_Corrective_Hint()
    {
        var codeFixService = A.Fake<ICodeFixService>();

        var result = await ApplyFixesTool.ApplyFixes(codeFixService, [], "TestProject");

        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("ValidationError");
        result.Error.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unrecognized_Severity_Returns_Validation_Error_With_Corrective_Hint()
    {
        var analyzerService = A.Fake<ISolutionAnalyzerService>();

        var result = await AnalyzeSolutionTool.AnalyzeSolution(analyzerService, "test.sln", severity: "critical");

        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe("ValidationError");
        result.Error.Hint.ShouldNotBeNull();
        result.Error.Hint.ShouldContain("Warning");

        // The service must never be called with a value the tool already rejected.
        A.CallTo(() => analyzerService.AnalyzeSolutionAsync(
                A<string>._, A<string?>._, A<string?>._, A<string?>._, A<string?>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("warning")]
    [InlineData("INFO")]
    [InlineData("Hidden")]
    [InlineData(null)]
    public async Task Recognized_Severity_Values_Are_Not_Rejected(string? severity)
    {
        var analyzerService = A.Fake<ISolutionAnalyzerService>();
        A.CallTo(() => analyzerService.AnalyzeSolutionAsync(
                A<string>._, A<string?>._, A<string?>._, A<string?>._, A<string?>._, A<int>._, A<IProgress<ProgressNotificationValue>?>._, A<CancellationToken>._))
            .Returns(Task.FromResult(new AnalyzeSolutionResponse()));

        var result = await AnalyzeSolutionTool.AnalyzeSolution(analyzerService, "test.sln", severity: severity);

        result.Ok.ShouldBeTrue();
        result.Error.ShouldBeNull();
    }

    private static void AssertDocumentedType<T>(ToolResult<T> result, string expectedType)
    {
        result.Ok.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.Type.ShouldBe(expectedType);
        DocumentedErrorTypes.ShouldContain(result.Error.Type);
    }
}
