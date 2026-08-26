using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Shouldly;

namespace RoselineMCP.Tests.Release;

/// <summary>
/// Pins the PR-title check workflow's regex, not just its presence. The workflow itself only runs
/// on GitHub — never inside this suite — so a change that silently regressed its coverage (rejects
/// the release PR's own <c>chore(dev): release X.Y.Z</c> title, or accepts a bare "wip") would
/// otherwise go unnoticed until it deadlocked a release or let a type-less title through. Extracting
/// the exact pattern the workflow tests against keeps the assertions honest about what ships,
/// rather than exercising a hand-copied duplicate that could drift from it (#164).
/// </summary>
public class PrTitleConventionTests
{
    private const string WorkflowPath = ".github/workflows/pr-title.yml";

    [Fact]
    public void Workflow_Should_Exist()
    {
        File.Exists(RepoPath(WorkflowPath)).ShouldBeTrue($"expected the PR-title check workflow at {WorkflowPath}");
    }

    [Theory]
    [InlineData("feat: x")]
    [InlineData("fix(loader): x")]
    [InlineData("feat!: x")]
    [InlineData("fix(loader/paths): x")]
    [InlineData("chore(dev): release 2.3.0")]
    public void Regex_Should_Accept_Conventional_Titles(string title)
    {
        WorkflowRegex().IsMatch(title).ShouldBeTrue($"'{title}' should be accepted as a Conventional Commit title.");
    }

    [Theory]
    [InlineData("wip")]
    [InlineData("Fix the exporter")]
    [InlineData("feature: x")]
    public void Regex_Should_Reject_NonConventional_Titles(string title)
    {
        WorkflowRegex().IsMatch(title).ShouldBeFalse($"'{title}' should be rejected as not a Conventional Commit title.");
    }

    /// <summary>
    /// Extracts the exact pattern the workflow tests the PR title against (a shell
    /// <c>PATTERN='...'</c> assignment), so these assertions exercise what actually ships rather
    /// than a copy that could drift from it.
    /// </summary>
    private static Regex WorkflowRegex()
    {
        var workflow = File.ReadAllText(RepoPath(WorkflowPath));
        var match = Regex.Match(workflow, "PATTERN=['\"](?<pattern>[^'\"]+)['\"]");

        match.Success.ShouldBeTrue("could not find a PATTERN='...' assignment in the PR-title workflow.");

        return new Regex(match.Groups["pattern"].Value);
    }

    /// <summary>
    /// Resolves a repository-relative path from this source file's compile-time location, the same
    /// idiom <see cref="ReleaseWorkflowTests"/> uses. This file lives at
    /// <c>RoselineMCP.Tests/Release/</c>, so the repository root is two levels up.
    /// </summary>
    private static string RepoPath(string relativePath, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", relativePath));
}
