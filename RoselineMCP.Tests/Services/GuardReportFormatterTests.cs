using RoselineMCP.Models;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="GuardReportFormatter"/> — the pure rendering step between a
/// <see cref="VerificationVerdict"/> and the text the compile guard hands back to an agent.
/// </summary>
/// <remarks>
/// The most important property under test is the one that reads like an omission: <b>the formatter
/// returns <see langword="null"/> whenever it has nothing to say.</b> The guard runs after every
/// file write, so a formatter that always produced text would spend tokens on the overwhelmingly
/// common "nothing broke" case and would be uninstalled within a day.
/// </remarks>
public class GuardReportFormatterTests
{
    private static DiagnosticDetail Error(string file, int line, int column, string id, string message) =>
        new()
        {
            Project = "Core",
            File = file,
            Line = line,
            Column = column,
            Id = id,
            Severity = "Error",
            Message = message,
        };

    [Fact]
    public void Says_Nothing_When_The_Edit_Introduced_No_Errors()
    {
        var verdict = new VerificationVerdict { Compiles = true, ScopeComplete = true };

        GuardReportFormatter.Format(verdict).ShouldBeNull();
    }

    [Fact]
    public void Says_Nothing_When_The_Only_Errors_Were_Already_There()
    {
        // Landing on a red branch must not produce a wall of errors the agent did not cause.
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Preexisting = 12,
            ScopeComplete = true,
        };

        GuardReportFormatter.Format(verdict).ShouldBeNull();
    }

    [Fact]
    public void Says_Nothing_When_The_Edit_Only_Resolved_Errors()
    {
        var verdict = new VerificationVerdict
        {
            Compiles = true,
            Preexisting = 2,
            Resolved = [Error("/repo/Core/Thing.cs", 3, 5, "CS0103", "The name 'x' does not exist")],
            ScopeComplete = true,
        };

        GuardReportFormatter.Format(verdict).ShouldBeNull();
    }

    [Fact]
    public void Names_File_Line_Column_Id_And_Message_For_Each_Introduced_Error()
    {
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Introduced =
            [
                Error("/repo/Core/Thing.cs", 12, 9, "CS0103", "The name 'x' does not exist in the current context"),
                Error("/repo/Mid/Middle.cs", 4, 20, "CS1501", "No overload for method 'Value' takes 1 arguments"),
            ],
            ScopeComplete = true,
        };

        var report = GuardReportFormatter.Format(verdict);

        report.ShouldNotBeNull();
        report.ShouldContain("/repo/Core/Thing.cs(12,9)");
        report.ShouldContain("CS0103");
        report.ShouldContain("The name 'x' does not exist in the current context");
        report.ShouldContain("/repo/Mid/Middle.cs(4,20)");
        report.ShouldContain("CS1501");
        report.ShouldContain("No overload for method 'Value' takes 1 arguments");
    }

    [Fact]
    public void States_How_Many_Errors_The_Edit_Introduced()
    {
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Introduced =
            [
                Error("/repo/Core/Thing.cs", 12, 9, "CS0103", "one"),
                Error("/repo/Core/Thing.cs", 13, 9, "CS0103", "two"),
            ],
            ScopeComplete = true,
        };

        GuardReportFormatter.Format(verdict).ShouldNotBeNull().ShouldContain("2");
    }

    [Fact]
    public void Reports_The_Omitted_Count_So_The_List_Never_Reads_As_Exhaustive()
    {
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Introduced = [Error("/repo/Core/Thing.cs", 12, 9, "CS0103", "boom")],
            Omitted = 417,
            ScopeComplete = true,
        };

        GuardReportFormatter.Format(verdict).ShouldNotBeNull().ShouldContain("417");
    }

    [Fact]
    public void Includes_The_Verdict_Notes_Verbatim()
    {
        const string note = "Loaded a bare .csproj with no containing solution; dependents outside it were not compiled.";
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Introduced = [Error("/repo/Core/Thing.cs", 12, 9, "CS0103", "boom")],
            ScopeComplete = false,
            Notes = [note],
        };

        GuardReportFormatter.Format(verdict).ShouldNotBeNull().ShouldContain(note);
    }

    [Fact]
    public void Says_The_Gate_Was_Partial_When_Scope_Is_Incomplete()
    {
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Introduced = [Error("/repo/Core/Thing.cs", 12, 9, "CS0103", "boom")],
            ScopeComplete = false,
        };

        // A partial gate must never read as a full one, even when the verdict carries no notes.
        GuardReportFormatter.Format(verdict).ShouldNotBeNull().ShouldContain("partial");
    }

    [Fact]
    public void Does_Not_Claim_Partial_Coverage_When_The_Scope_Was_Complete()
    {
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Introduced = [Error("/repo/Core/Thing.cs", 12, 9, "CS0103", "boom")],
            ScopeComplete = true,
        };

        GuardReportFormatter.Format(verdict).ShouldNotBeNull().ShouldNotContain("partial");
    }

    [Fact]
    public void Caps_Its_Output_Well_Inside_The_Harness_Feedback_Limit()
    {
        // The harness truncates hook feedback at 10,000 characters; being truncated by someone
        // else means losing the trailing explanation, so the formatter does its own trimming.
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Introduced = [.. Enumerable.Range(1, 2000).Select(i =>
                Error($"/repo/Core/VeryLongFileName{i}.cs", i, 9, "CS0103", new string('x', 200)))],
            ScopeComplete = true,
        };

        var report = GuardReportFormatter.Format(verdict);

        report.ShouldNotBeNull();
        report.Length.ShouldBeLessThanOrEqualTo(GuardReportFormatter.MaxReportLength);
        GuardReportFormatter.MaxReportLength.ShouldBeLessThan(10_000);
        report.ShouldContain("truncated");
    }

    [Fact]
    public void Truncation_Never_Splits_A_Diagnostic_Line()
    {
        var verdict = new VerificationVerdict
        {
            Compiles = false,
            Introduced = [.. Enumerable.Range(1, 2000).Select(i =>
                Error($"/repo/Core/File{i}.cs", i, 9, "CS0103", new string('x', 200)))],
            ScopeComplete = true,
        };

        var report = GuardReportFormatter.Format(verdict).ShouldNotBeNull();

        // Every rendered diagnostic line is whole: none ends in the middle of a padded message.
        foreach (var line in report.Split('\n').Where(l => l.Contains("CS0103")))
        {
            line.TrimEnd().ShouldEndWith(new string('x', 200));
        }
    }
}
