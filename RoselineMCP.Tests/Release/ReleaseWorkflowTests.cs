using System.Runtime.CompilerServices;
using Shouldly;

namespace RoselineMCP.Tests.Release;

/// <summary>
/// Guards on the release workflow's structure. Every assertion here corresponds to a failure mode
/// that produces <b>no failed workflow run</b>: the release simply does not happen, or happens
/// without publishing, while every step reports green. That is what makes them worth pinning in
/// tests rather than trusting to review — a broken release is invisible until someone goes looking
/// for a package that was never pushed.
/// </summary>
public class ReleaseWorkflowTests
{
    private const string ReleaseWorkflowPath = ".github/workflows/release-please.yml";

    /// <summary>
    /// release-please finds a merged release PR <i>solely</i> by the <c>autorelease: pending</c>
    /// label, and this repository has no <c>autorelease:*</c> label, so the action has to create
    /// one — a POST to the Issues-scoped labels endpoint. Without <c>issues: write</c> the release
    /// PR still opens and can still be merged, but is never recognised afterwards: no tag, no
    /// GitHub Release, <c>release_created</c> stays false, the publish and docker jobs skip, and
    /// nothing is published — with no failed step anywhere.
    /// </summary>
    [Fact]
    public void ReleaseWorkflow_Should_Request_IssuesWrite_For_The_Autorelease_Label()
    {
        ReadRepoFile(ReleaseWorkflowPath).ShouldContain("issues: write");
    }

    /// <summary>
    /// MinVer derives the package version from the git tag, so the publish job must check out the
    /// tag itself <i>and</i> fetch the full history. A shallow checkout of the default ref resolves
    /// no tag, packs <c>0.0.0-alpha.0.N</c>, and pushes that to nuget.org — which delists but never
    /// deletes.
    /// </summary>
    [Fact]
    public void PublishJob_Should_CheckOut_The_Tag_With_Full_History()
    {
        var workflow = ReadRepoFile(ReleaseWorkflowPath);

        workflow.ShouldContain("ref: ${{ needs.release-please.outputs.tag_name }}");
        workflow.ShouldContain("fetch-depth: 0");
    }

    /// <summary>
    /// The NuGet login step must not be skipped when <c>NUGET_USER</c> is unset. The two guards
    /// fail in opposite directions: <c>release_created == false</c> means nothing was meant to
    /// publish, so skipping is right; a missing <c>NUGET_USER</c> means a release <i>was</i> meant
    /// to publish and could not, and skipping there reports the run green on a version whose tag,
    /// changelog and GitHub Release already exist. Released-but-unpublished-and-green is the worst
    /// outcome for a path exercised a handful of times a year, so a missing secret must fail the
    /// step instead.
    /// </summary>
    [Fact]
    public void NuGetLogin_Should_Not_Be_Gated_On_The_NuGetUser_Secret()
    {
        // Comments stripped deliberately: the workflow explains *why* this guard is absent, and it
        // has to name the guard to do so. An assertion about code must not be satisfied — or
        // defeated — by prose.
        var workflow = WithoutComments(ReadRepoFile(ReleaseWorkflowPath));

        workflow.ShouldNotContain("env.NUGET_USER != ''");
        workflow.ShouldNotContain("secrets.NUGET_USER != ''");
    }

    /// <summary>
    /// GitHub deliberately does not fire <c>on: push: tags</c> (or <c>on: release</c>) for events
    /// created by <c>GITHUB_TOKEN</c>, and release-please creates the tag with exactly that token.
    /// Any publishing workflow left on a tag trigger would therefore never run again — silently,
    /// with no failed run to notice. This asserts on the <b>trigger section only</b>: a bare search
    /// for <c>tags:</c> would also match <c>docker/metadata-action</c>'s <c>tags:</c> input, which
    /// is an ordinary job input and entirely benign.
    /// </summary>
    [Fact]
    public void NoWorkflow_Should_Trigger_From_A_Tag_Push()
    {
        var workflowDirectory = RepoPath(".github/workflows");

        foreach (var file in Directory.GetFiles(workflowDirectory, "*.yml"))
        {
            WithoutComments(TriggerSection(File.ReadAllText(file)))
                .ShouldNotContain("tags:", Case.Sensitive, $"{Path.GetFileName(file)} triggers from a tag push, which a GITHUB_TOKEN-created tag never fires.");
        }
    }

    /// <summary>
    /// Drops whole-line YAML comments so the assertions above read the workflow's behavior rather
    /// than the prose describing it.
    /// </summary>
    private static string WithoutComments(string workflow) =>
        string.Join('\n', workflow.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>
    /// Everything a workflow declares before its <c>jobs:</c> key — which is where the <c>on:</c>
    /// triggers live. Splitting on the top-level key keeps the tag-trigger assertion away from
    /// job-level inputs that happen to share a name.
    /// </summary>
    private static string TriggerSection(string workflow)
    {
        var jobsIndex = workflow.IndexOf("\njobs:", StringComparison.Ordinal);

        return jobsIndex < 0 ? workflow : workflow[..jobsIndex];
    }

    private static string ReadRepoFile(string relativePath) => File.ReadAllText(RepoPath(relativePath));

    /// <summary>
    /// Resolves a repository-relative path from this source file's compile-time location, the same
    /// idiom <c>ToolSchemaSnapshotTests</c> uses to reach its snapshot. This file lives at
    /// <c>RoselineMCP.Tests/Release/</c>, so the repository root is two levels up.
    /// </summary>
    private static string RepoPath(string relativePath, [CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", relativePath));
}
