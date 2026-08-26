using Shouldly;

namespace RoselineMCP.Tests.Release;

/// <summary>
/// Guards on the website build/deploy workflows. <c>website/package-lock.json</c> and
/// <c>package.json</c> can drift out of sync silently — <c>npm ci</c> then fails, and until #190
/// <c>deploy-docs.yml</c> masked that failure with an <c>npm install</c> fallback, so the deploy
/// step "succeeded" while quietly no longer honoring the committed lockfile. These tests pin the
/// workflow text so that failure mode is a red test rather than a silent fallback.
/// </summary>
public class WebsiteWorkflowTests
{
    private const string DeployDocsWorkflowPath = ".github/workflows/deploy-docs.yml";
    private const string CiWorkflowPath = ".github/workflows/ci.yml";

    /// <summary>
    /// <c>npm ci || npm install</c> means a stale/out-of-sync lockfile never fails the deploy — it
    /// silently falls back to <c>npm install</c>, which rewrites the lockfile in the runner and
    /// builds from it, so the drift never surfaces as a red check. The install step must be a bare
    /// <c>npm ci</c> so drift fails loudly.
    /// </summary>
    [Fact]
    public void Deploy_Docs_Installs_With_npm_ci_And_Has_No_npm_install_Fallback()
    {
        var workflow = ReadRepositoryFile(DeployDocsWorkflowPath);

        workflow.ShouldContain("run: npm ci");
        workflow.ShouldNotContain("npm install");
    }

    /// <summary>
    /// The lockfile can only be trusted if something builds against it on every PR, before a merge
    /// to <c>dev</c> ever reaches <c>deploy-docs.yml</c>. <c>ci.yml</c> must run <c>npm ci</c> then
    /// <c>npm run build</c> from a <c>website</c> working directory so drift is caught as a red
    /// check on the PR itself.
    /// </summary>
    [Fact]
    public void Ci_Builds_The_Website_With_npm_ci()
    {
        var workflow = ReadRepositoryFile(CiWorkflowPath);

        workflow.ShouldContain("working-directory: website");
        workflow.ShouldContain("run: npm ci");
        workflow.ShouldContain("run: npm run build");
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the repository root — the directory
    /// holding <c>RoselineMCP.sln</c> — and resolves <paramref name="relativePath"/> against it.
    /// Independent of the current working directory the test runner was launched from.
    /// </summary>
    internal static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RoselineMCP.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"Could not locate RoselineMCP.sln by walking up from {AppContext.BaseDirectory}.");
        }

        return Path.Combine(directory.FullName, relativePath);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(FindRepositoryFile(relativePath));
}
