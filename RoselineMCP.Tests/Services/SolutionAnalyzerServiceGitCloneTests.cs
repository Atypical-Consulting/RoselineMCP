using System.Diagnostics;
using System.Reflection;
using FakeItEasy;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Exercises <c>SolutionAnalyzerService.CloneGitRepositoryAsync</c> — the real Git-clone
/// mechanism — directly via reflection against a local, on-disk repository fixture.
///
/// This is deliberately the "testable seam" that sits *behind* the production http(s)-only URL
/// gate (<c>IsGitUrl</c>, covered separately in <see cref="SolutionAnalyzerServiceInternalTests"/>):
/// real callers can only ever reach the clone mechanism through an http(s) URL, but the clone
/// mechanism itself doesn't re-validate the scheme, so it can be exercised here against a local
/// path with no live network access and without weakening that production restriction.
/// </summary>
public class SolutionAnalyzerServiceGitCloneTests : IDisposable
{
    private readonly SolutionAnalyzerService _sut;
    private readonly string _testDirectory;

    public SolutionAnalyzerServiceGitCloneTests()
    {
        var logger = A.Fake<ILogger<SolutionAnalyzerService>>();
        var msBuildService = A.Fake<IMSBuildService>();
        var filterService = A.Fake<IDiagnosticFilterService>();
        _sut = new SolutionAnalyzerService(logger, msBuildService, filterService);

        _testDirectory = Path.Combine(Path.GetTempPath(), $"RoselineGitCloneTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
    }

    public void Dispose()
    {
        try { ForceDeleteDirectory(_testDirectory); } catch { /* ignored */ }
    }

    /// <summary>
    /// Git marks object/pack files read-only on Windows, which a plain Directory.Delete(true)
    /// refuses to remove (UnauthorizedAccessException) — clear the attribute first, mirroring
    /// the fix applied to the production SafeDeleteDirectory this test suite exercises.
    /// </summary>
    private static void ForceDeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(directory, recursive: true);
    }

    private async Task<T> InvokePrivateAsync<T>(string methodName, params object?[] args)
    {
        var method = typeof(SolutionAnalyzerService).GetMethod(
            methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        method.ShouldNotBeNull($"Method '{methodName}' not found");
        var task = (Task<T>)method!.Invoke(_sut, args)!;
        return await task;
    }

    /// <summary>
    /// Creates a tiny local Git repository fixture on disk containing a minimal .sln, commits
    /// it, and returns the repo's local path — used purely as the clone *source* in these
    /// tests, never as a URL that passes through the production <c>IsGitUrl</c> gate.
    /// </summary>
    private string CreateSourceRepo(string name = "source")
    {
        var repoDir = Path.Combine(_testDirectory, name);
        Directory.CreateDirectory(repoDir);

        RunGit(repoDir, "init");
        File.WriteAllText(
            Path.Combine(repoDir, "Fixture.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00");
        RunGit(repoDir, "add -A");
        RunGit(repoDir, "-c user.email=test@example.com -c user.name=Test commit -m init");

        return repoDir;
    }

    private static void RunGit(string workingDirectory, string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"git {arguments} failed: {stderr}");
        }
    }

    [Fact]
    public async Task Should_Clone_Local_Repository_Into_New_Temp_Directory()
    {
        // Arrange
        var sourceRepo = CreateSourceRepo();

        // Act — local path used directly as the clone source, bypassing IsGitUrl on purpose.
        var clonedDirectory = await InvokePrivateAsync<string>(
            "CloneGitRepositoryAsync", sourceRepo, null, CancellationToken.None);

        try
        {
            // Assert
            clonedDirectory.ShouldNotBeNullOrWhiteSpace();
            clonedDirectory.ShouldNotBe(sourceRepo);
            Directory.Exists(clonedDirectory).ShouldBeTrue();
            File.Exists(Path.Combine(clonedDirectory, "Fixture.sln")).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(clonedDirectory))
            {
                ForceDeleteDirectory(clonedDirectory);
            }
        }
    }

    [Fact]
    public async Task Should_Clone_Requested_Branch_When_Specified()
    {
        // Arrange — a second branch with an extra file that only exists there.
        var sourceRepo = CreateSourceRepo();
        RunGit(sourceRepo, "checkout -b feature-branch");
        File.WriteAllText(Path.Combine(sourceRepo, "OnlyOnFeature.txt"), "feature content");
        RunGit(sourceRepo, "add -A");
        RunGit(sourceRepo, "-c user.email=test@example.com -c user.name=Test commit -m feature");

        // Act
        var clonedDirectory = await InvokePrivateAsync<string>(
            "CloneGitRepositoryAsync", sourceRepo, "feature-branch", CancellationToken.None);

        try
        {
            // Assert — the file only committed on feature-branch is present in the clone.
            File.Exists(Path.Combine(clonedDirectory, "OnlyOnFeature.txt")).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(clonedDirectory))
            {
                ForceDeleteDirectory(clonedDirectory);
            }
        }
    }

    [Fact]
    public async Task Should_Throw_Descriptive_Error_And_Clean_Up_For_Nonexistent_Source()
    {
        // Arrange
        var nonExistentSource = Path.Combine(_testDirectory, "does-not-exist");
        var orphansBefore = CountOrphanedCloneTempDirectories();

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await InvokePrivateAsync<string>(
                "CloneGitRepositoryAsync", nonExistentSource, null, CancellationToken.None));

        exception.Message.ShouldContain("Failed to clone Git repository");
        // The message should carry git's own diagnostic, not just a bare process-exit-code.
        exception.Message.ShouldNotBe("Failed to clone Git repository: ");

        // Assert — the temp clone directory created for this failed attempt was actually
        // deleted, not just that a descriptive error was thrown.
        var orphansAfter = CountOrphanedCloneTempDirectories();
        orphansAfter.ShouldBe(orphansBefore, "the failed clone should not leave an orphaned temp directory behind");
    }

    /// <summary>
    /// Counts directories under the OS temp directory matching the
    /// <c>roselinemcp-clone-*</c> pattern that <c>CloneGitRepositoryAsync</c> creates. Used as
    /// a black-box way to verify cleanup happened without exposing the temp dir path from
    /// production code purely for testability.
    /// </summary>
    private static int CountOrphanedCloneTempDirectories()
    {
        return Directory.GetDirectories(Path.GetTempPath(), "roselinemcp-clone-*", SearchOption.TopDirectoryOnly).Length;
    }

    [Fact]
    public async Task Should_Throw_OperationCanceledException_When_Token_Already_Cancelled()
    {
        // Arrange
        var sourceRepo = CreateSourceRepo();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert — proves cancellation is honored rather than the clone silently
        // running to completion; deterministic alternative to timing a real timeout.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await InvokePrivateAsync<string>(
                "CloneGitRepositoryAsync", sourceRepo, null, cts.Token));
    }
}
