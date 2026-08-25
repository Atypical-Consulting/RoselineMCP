using System.Text.RegularExpressions;
using RoselineMCP.Tests.Services;
using Shouldly;

namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Pins the membership of <see cref="ProcessEnvironmentCollection"/>: every test class in a file that
/// mutates the process environment must sit in that collection, and that collection must still
/// disable parallelization.
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
/// a class that started scoping the environment would have reintroduced the race in silence.
/// </para>
/// <para>
/// The scan reads <b>source text</b> rather than reflecting over types, because the hazard is
/// <i>which code calls the helpers</i>. Reflection can see that a class carries a collection
/// attribute; it cannot see that a method body opens an environment scope — the helpers are
/// <c>internal</c> and the calls are ordinary statements that leave no trace in metadata.
/// </para>
/// <para>
/// The rule is deliberately <b>per class, and whole-file</b>: once any code in a file mutates the
/// environment, <i>every</i> class declared in that file must carry the attribute. Asserting once per
/// file would be the hole this test exists to close — <c>ScopedEnvironmentVariableTests.cs</c> already
/// declares two classes, so a third added without the attribute would inherit a green bar from its
/// neighbours and get its own parallel collection anyway. Requiring it of every class is
/// over-inclusive by design: it is trivially satisfied, and it needs no judgement about which class in
/// a shared file owns the call.
/// </para>
/// <para>
/// Known limitation, inherent to a source scan: the tree walked is whatever sits above
/// <see cref="AppContext.BaseDirectory"/>, which under <c>--no-build</c> after a branch switch need
/// not be the tree the running assembly was compiled from. The same limitation applies to
/// <see cref="AnalyzerReferenceLoadTests.FindRepositoryRoot"/>, which this shares.
/// </para>
/// </remarks>
public partial class ProcessEnvironmentCollectionTests
{
    /// <summary>The helper's own file: it <i>defines</i> the calls below rather than making them.</summary>
    private const string HelperRelativePath = "Configuration/ScopedEnvironmentVariable.cs";

    /// <summary>This file: its needles would otherwise match themselves.</summary>
    private const string ThisRelativePath = "Configuration/ProcessEnvironmentCollectionTests.cs";

    /// <summary>
    /// What makes a file an environment mutator. The raw framework call is listed alongside the two
    /// helpers because it is the idiom the helpers replaced, and the first thing someone unaware of
    /// them writes — it races identically and would otherwise be invisible to this scan.
    /// </summary>
    private static readonly string[] EnvironmentMutations =
    [
        "ScopedEnvironmentVariable.Set(",
        "ScopedEnvironmentNamespace.Clear(",
        "Environment.SetEnvironmentVariable(",
    ];

    /// <summary>
    /// The classes known to mutate the environment when this test was written. Asserting they are
    /// still found is what stops the scan from passing vacuously — a rename, a moved file or a typo
    /// in a needle would otherwise leave it green over an empty set.
    /// </summary>
    private static readonly string[] KnownMutatorFiles =
    [
        "Configuration/RoselineMcpOptionsBindingTests.cs",
        "Configuration/GuardOptionsTests.cs",
        "Configuration/ScopedEnvironmentVariableTests.cs",
    ];

    /// <summary>A top-level class declaration, capturing its name.</summary>
    [GeneratedRegex(@"^[ \t]*(?:(?:public|internal|sealed|abstract|static|partial)[ \t]+)*class[ \t]+(?<name>\w+)",
        RegexOptions.Multiline)]
    private static partial Regex ClassDeclaration();

    /// <summary>
    /// Membership in this collection, in any spelling xunit.v3 accepts — the string form this repo
    /// uses, plus <c>CollectionAttribute&lt;T&gt;</c> and <c>CollectionAttribute(Type)</c>, which are
    /// equally valid and would otherwise fail a caller for an attribute they already carry.
    /// </summary>
    [GeneratedRegex(@"\[\s*(?:Xunit\s*\.\s*)?Collection\s*(?:<\s*ProcessEnvironmentCollection\s*>\s*(?:\(\s*\))?|\(\s*(?:ProcessEnvironmentCollection\s*\.\s*Name|typeof\s*\(\s*ProcessEnvironmentCollection\s*\))\s*\))\s*\]")]
    private static partial Regex CollectionMembership();

    [Fact]
    public void Every_Class_That_Scopes_The_Environment_Is_In_The_Sequential_Collection()
    {
        var root = AnalyzerReferenceLoadTests.FindRepositoryRoot();
        var testProjectRoot = Path.Combine(root.FullName, "RoselineMCP.Tests");
        Directory.Exists(testProjectRoot).ShouldBeTrue(
            $"expected the test project's sources at {testProjectRoot}");

        var sources = EnumerateSources(testProjectRoot)
            .Select(path => (
                Relative: Path.GetRelativePath(testProjectRoot, path).Replace('\\', '/'),
                Text: File.ReadAllText(path)))
            .ToList();

        sources.ShouldNotBeEmpty($"expected C# sources under {testProjectRoot}");

        // Both exclusions are named by full relative path, so a same-named file elsewhere in the tree
        // is still scanned, and renaming either one fails here rather than silently covering nothing.
        foreach (var excluded in new[] { HelperRelativePath, ThisRelativePath })
        {
            sources.ShouldContain(
                file => file.Relative == excluded,
                $"'{excluded}' is excluded from the scan by path — it must still exist, or the " +
                "exclusion is silently covering nothing");
        }

        var mutators = sources
            .Where(file => file.Relative != HelperRelativePath && file.Relative != ThisRelativePath)
            .Where(file => EnvironmentMutations.Any(
                call => file.Text.Contains(call, StringComparison.Ordinal)))
            .ToList();

        var mutatorPaths = mutators.Select(file => file.Relative).ToList();
        mutatorPaths.ShouldNotBeEmpty(
            "the scan found no environment-mutating file at all — the needles no longer match " +
            "anything, so this test would pass vacuously");

        foreach (var known in KnownMutatorFiles)
        {
            mutatorPaths.ShouldContain(known, $"'{known}' mutates the process environment");
        }

        foreach (var (relative, text) in mutators)
        {
            var declarations = ClassDeclaration().Matches(text);
            declarations.Count.ShouldBeGreaterThan(
                0, $"{relative} mutates the environment but declares no class this test can check");

            // Each class is judged on the text since the previous declaration — where its own
            // attribute list sits. Checking the file as a whole is what would let a second class in
            // an already-attributed file through.
            var searchedFrom = 0;
            foreach (Match declaration in declarations)
            {
                var preamble = text[searchedFrom..declaration.Index];
                CollectionMembership().IsMatch(preamble).ShouldBeTrue(
                    $"{relative} opens a scoped environment mutation, so its class " +
                    $"'{declaration.Groups["name"].Value}' must carry " +
                    $"[Collection({nameof(ProcessEnvironmentCollection)}.Name)] — otherwise xunit " +
                    "runs it in parallel with the other mutators and their save/restore of " +
                    "process-global state can interleave");

                searchedFrom = declaration.Index + declaration.Length;
            }
        }
    }

    /// <summary>
    /// Sharing a collection is only half the mechanism; without this flag the collection still runs
    /// in parallel with every other collection, and <see cref="ScopedEnvironmentNamespace"/> needs
    /// more than mutual exclusion between its own members — it deletes every variable under the
    /// prefix and section for the duration of the scope, so nothing else may be reading them.
    /// </summary>
    [Fact]
    public void The_Collection_Definition_Still_Disables_Parallelization()
    {
        var definitions = typeof(ProcessEnvironmentCollection)
            .GetCustomAttributes(typeof(CollectionDefinitionAttribute), inherit: false)
            .Cast<CollectionDefinitionAttribute>()
            .ToList();

        definitions.Count.ShouldBe(
            1, $"{nameof(ProcessEnvironmentCollection)} must carry exactly one [CollectionDefinition]");
        definitions[0].Name.ShouldBe(ProcessEnvironmentCollection.Name);
        definitions[0].DisableParallelization.ShouldBeTrue(
            "without it the collection still runs in parallel with every other collection, and a " +
            "namespace-wide Clear is not safe against a concurrent reader");
    }

    /// <summary>
    /// Every <c>.cs</c> file the test project actually owns. <c>bin</c>/<c>obj</c> are skipped at the
    /// project root — where MSBuild is the only thing that writes them — rather than at any depth, so
    /// a real source directory that happens to be called <c>bin</c> stays in the audit.
    /// </summary>
    private static IEnumerable<string> EnumerateSources(string testProjectRoot) =>
        Directory.EnumerateFiles(testProjectRoot, "*.cs", SearchOption.TopDirectoryOnly)
            .Concat(Directory
                .EnumerateDirectories(testProjectRoot)
                .Where(directory => Path.GetFileName(directory) is var name
                    && !name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                .SelectMany(directory =>
                    Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)));
}