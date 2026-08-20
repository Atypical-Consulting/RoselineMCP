using System.Text.Json;
using ModelContextProtocol;
using RoselineMCP.Models;
using Shouldly;

namespace RoselineMCP.Tests.Models;

/// <summary>
/// Wire-contract tests for <see cref="VerificationVerdict"/>. The verdict rides inside every write
/// tool's response and is <c>check_compilation</c>'s whole payload, so its JSON names are a public
/// contract — and its omissions are the point: a verdict that emitted eight always-present fields
/// would cost tokens on every single edit, which is the opposite of why this server exists.
/// Serialized with the SDK's own options so the assertions are about the bytes a model reads.
/// </summary>
public class VerificationVerdictTests
{
    private static readonly JsonSerializerOptions Wire = McpJsonUtilities.DefaultOptions;

    [Fact]
    public void Should_Use_CamelCase_Wire_Names()
    {
        // Arrange
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Errors = [new DiagnosticDetail { Id = "CS0103", File = "src/A.cs", Line = 12, Column = 5 }],
            Introduced = [new DiagnosticDetail { Id = "CS0103" }],
            Resolved = [new DiagnosticDetail { Id = "CS0168" }],
            Preexisting = 3,
            Omitted = 7,
            Scope = ["Core", "Web"],
            ScopeComplete = true,
            Notes = ["a note"]
        };

        // Act
        var json = JsonSerializer.Serialize(verdict, Wire);

        // Assert
        json.ShouldContain("\"compiles\":false");
        json.ShouldContain("\"errors\":[");
        json.ShouldContain("\"introduced\":[");
        json.ShouldContain("\"resolved\":[");
        json.ShouldContain("\"preexisting\":3");
        json.ShouldContain("\"omitted\":7");
        json.ShouldContain("\"scope\":[\"Core\",\"Web\"]");
        json.ShouldContain("\"scopeComplete\":true");
        json.ShouldContain("\"notes\":[\"a note\"]");
    }

    [Fact]
    public void Should_Omit_Empty_Collections_And_Null_Compiles()
    {
        // Arrange — the shape of a clean delta verdict: nothing introduced, nothing resolved,
        // no absolute error list, no compilation performed.
        var verdict = new VerificationVerdict();

        // Act
        var json = JsonSerializer.Serialize(verdict, Wire);

        // Assert
        json.ShouldNotContain("compiles");
        json.ShouldNotContain("errors");
        json.ShouldNotContain("introduced");
        json.ShouldNotContain("resolved");
        json.ShouldNotContain("preexisting");
        json.ShouldNotContain("omitted");
        json.ShouldNotContain("scope\"");
        json.ShouldNotContain("notes");
    }

    [Fact]
    public void Should_Always_Report_ScopeComplete_Even_When_False()
    {
        // A false scopeComplete is the interesting case — the gate ran, but could not prove it saw
        // every dependent. Omitting it would make "partial gate" indistinguishable from "full gate",
        // which is the false green this field exists to prevent.
        var json = JsonSerializer.Serialize(new VerificationVerdict { ScopeComplete = false }, Wire);

        json.ShouldContain("\"scopeComplete\":false");
    }

    [Fact]
    public void Should_Default_To_A_Verdict_That_Claims_Nothing()
    {
        // Act
        var verdict = new VerificationVerdict();

        // Assert
        verdict.Compiles.ShouldBeNull();
        verdict.Errors.ShouldBeNull();
        verdict.Introduced.ShouldBeNull();
        verdict.Resolved.ShouldBeNull();
        verdict.Notes.ShouldBeNull();
        verdict.Scope.ShouldBeNull();
        verdict.Preexisting.ShouldBe(0);
        verdict.Omitted.ShouldBe(0);
        verdict.ScopeComplete.ShouldBeFalse();
    }
}
