using Shouldly;

namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Pins the membership of <see cref="ProcessEnvironmentCollection"/>: every test class that mutates
/// the process environment through the scoped helpers must sit in that collection, which is the only
/// thing stopping xunit from running them concurrently.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScopedEnvironmentVariable"/> and <see cref="ScopedEnvironmentNamespace"/> save and
/// restore process-global state, so they are correct only under strictly nested, single-threaded use.
/// xunit runs each test class as its own collection, in parallel, unless the classes share a
/// collection whose definition disables parallelization — and two classes clearing the same
/// <c>ROSELINE_</c> to <c>RoselineMCP:</c> namespace at once is what made
/// <c>RoselineMcpOptionsBindingTests.An_Ambient_All_Caps_Export_Cannot_Change_What_These_Tests_See</c>
/// go red once in four full runs (#189). Until this test existed the rule lived only in a remark, so
/// a fourth class that started scoping the environment would have reintroduced the race in silence.
/// </para>
/// <para>
/// The scan reads <b>source text</b> rather than reflecting over types, because the hazard is
/// <i>which code calls the helpers</i>. Reflection can see that a class carries a collection
/// attribute; it cannot see that a method body opens an environment scope — the helpers are
/// <c>internal</c> and the calls are ordinary statements that leave no trace in metadata.
/// </para>
/// </remarks>
public class ProcessEnvironmentCollectionTests
{
    /// <summary>The helper's own file: it <i>defines</i> the calls below rather than making them.</summary>
    private const string HelperFileName = "ScopedEnvironmentVariable.cs";

    /// <summary>This file: its needles would otherwise match themselves.</summary>
    private const string ThisFileName = "ProcessEnvironmentCollectionTests.cs";

    private const string RequiredAttribute = "[Collection(ProcessEnvironmentCollection.Name)]";

    /// <summary>Opening either scope is what makes a file an environment mutator.</summary>
    private static readonly string[] EnvironmentScopeCalls =
    [
        "ScopedEnvironmentVariable.Set(",
        "ScopedEnvironmentNamespace.Clear(",
    ];

    /// <summary>
    /// The classes known to mutate the environment when this test was written. Asserting they are
    /// still found is what stops the scan from passing vacuously — a rename, a moved file or a typo
    /// in a needle would otherwise leave it green over an empty set.
    /// </summary>
    private static readonly string[] KnownMutatorFiles =
    [
        "RoselineMcpOptionsBindingTests.cs",
        "GuardOptionsTests.cs",
        "ScopedEnvironmentVariableTests.cs",
    ];

    [Fact]
    public void Every_Class_That_Scopes_The_Environment_Is_In_The_Sequential_Collection()
    {
        var testProjectRoot = FindTestProjectRoot();

        var sources = Directory
            .EnumerateFiles(testProjectRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(testProjectRoot, path))
            .ToList();

        sources.ShouldNotBeEmpty($"expected C# sources under {testProjectRoot}");

        // Both exclusions are named rather than pattern-matched, so renaming either file fails here
        // instead of silently dropping a file from the scan or re-admitting one that matches itself.
        foreach (var excluded in new[] { HelperFileName, ThisFileName })
        {
            sources.ShouldContain(
                path => Path.GetFileName(path) == excluded,
                $"'{excluded}' is excluded from the scan by name — it must still exist, or the " +
                "exclusion is silently covering nothing");
        }

        var mutators = sources
            .Where(path => Path.GetFileName(path) != HelperFileName
                && Path.GetFileName(path) != ThisFileName)
            .Select(path => (Path: path, Text: File.ReadAllText(path)))
            .Where(file => EnvironmentScopeCalls.Any(
                call => file.Text.Contains(call, StringComparison.Ordinal)))
            .ToList();

        var mutatorNames = mutators.Select(file => Path.GetFileName(file.Path)).ToList();
        mutatorNames.ShouldNotBeEmpty(
            "the scan found no environment-mutating file at all — the needles no longer match " +
            "anything, so this test would pass vacuously");

        foreach (var known in KnownMutatorFiles)
        {
            mutatorNames.ShouldContain(known, $"'{known}' mutates the process environment");
        }

        foreach (var (path, text) in mutators)
        {
            text.ShouldContain(
                RequiredAttribute,
                customMessage:
                $"{Path.GetFileName(path)} opens a scoped environment mutation, so every test class " +
                $"in it must carry {RequiredAttribute} — otherwise xunit runs it in parallel with " +
                "the other mutators and their save/restore of process-global state can interleave");
        }
    }

    /// <summary>
    /// Walks up from the test output directory to the repository root (the directory holding
    /// <c>RoselineMCP.sln</c>) and returns the test project's source directory — the same ascent
    /// <c>AnalyzerReferenceLoadTests.FindRepositoryProject</c> makes.
    /// </summary>
    private static string FindTestProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoselineMCP.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("RoselineMCP.sln must be an ancestor of the test output directory");
        var testProjectRoot = Path.Combine(dir.FullName, "RoselineMCP.Tests");
        Directory.Exists(testProjectRoot).ShouldBeTrue(
            $"expected the test project's sources at {testProjectRoot}");
        return testProjectRoot;
    }

    /// <summary>
    /// True for anything under <c>bin/</c> or <c>obj/</c> — build output and generated sources are
    /// not the code whose collection membership is being pinned.
    /// </summary>
    private static bool IsBuildOutput(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
}
