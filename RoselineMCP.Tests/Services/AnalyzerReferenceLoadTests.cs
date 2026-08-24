using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Ground truth for #183: some of a real project's <see cref="Project.AnalyzerReferences"/>
/// contribute <b>no</b> C# analyzers. Roslyn signals that by an <em>empty array</em> — not an
/// exception — so a reference that failed to load (an analyzer built against a newer
/// <c>Microsoft.CodeAnalysis</c> than the one in-process) is indistinguishable from one that
/// genuinely carries none (a source-generator-only assembly), and a diagnostics pass that only
/// guards the throwing path walks past both in silence.
///
/// These tests describe reality rather than the product: they exist so a later refactor cannot
/// delete the evidence, and so the fix can be judged against a measured baseline. They load this
/// repository's own <c>RoselineMCP.csproj</c> through the production <see cref="ProjectLoader"/>,
/// which is the universal case — any <c>net5.0+</c> project carries the SDK's own analyzer set.
/// </summary>
public class AnalyzerReferenceLoadTests
{
    private readonly ITestOutputHelper _output;

    public AnalyzerReferenceLoadTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Walks up from the test output directory to the repository root (the directory holding
    /// <c>RoselineMCP.sln</c>) and returns the absolute path of <c>RoselineMCP/RoselineMCP.csproj</c>.
    /// </summary>
    internal static string FindRepositoryProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoselineMCP.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("RoselineMCP.sln must be an ancestor of the test output directory");
        var csproj = Path.Combine(dir.FullName, "RoselineMCP", "RoselineMCP.csproj");
        File.Exists(csproj).ShouldBeTrue($"expected the repository's own project at {csproj}");
        return csproj;
    }

    /// <summary>Loads the repository's own project through the production loader.</summary>
    internal static Task<LoadedProject> LoadRepositoryProjectAsync()
    {
        var msBuildService = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        var loader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuildService);
        return loader.LoadAsync(FindRepositoryProject(), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The <c>Microsoft.CodeAnalysis</c> version an assembly on disk binds against, read from its
    /// metadata tables without loading it — loading a foreign analyzer assembly into the test
    /// process is exactly the side effect this test should not have. <see langword="null"/> when
    /// the assembly does not reference <c>Microsoft.CodeAnalysis</c> at all.
    /// </summary>
    internal static Version? ReadReferencedRoslynVersion(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            if (metadata.GetString(reference.Name) == "Microsoft.CodeAnalysis")
            {
                return reference.Version;
            }
        }

        return null;
    }

    [Fact]
    public async Task References_That_Yield_No_Analyzers_Are_Identified()
    {
        // Arrange — the repository's own project, loaded the way the tools load it.
        using var loaded = await LoadRepositoryProjectAsync();
        var project = loaded.Project;
        project.AnalyzerReferences.ShouldNotBeEmpty(
            "an SDK-style net10.0 project always carries analyzer references (the SDK's own set)");

        // Act — what Roslyn answers for each reference. No exception is thrown on a load failure;
        // the answer is simply an empty array.
        var silent = new List<AnalyzerReference>();
        foreach (var reference in project.AnalyzerReferences)
        {
            var analyzers = reference.GetAnalyzers(LanguageNames.CSharp);
            _output.WriteLine($"{reference.Display}: {analyzers.Length} C# analyzer(s)");
            if (analyzers.IsEmpty)
            {
                silent.Add(reference);
            }
        }

        // Assert — the ground truth the product currently ignores: at least one reference
        // contributes nothing, and nothing about the call distinguishes "failed to load" from
        // "has none".
        silent.ShouldNotBeEmpty(
            "at least one analyzer reference of a real project yields no C# analyzers " +
            "(a source-generator-only assembly does, and so does an analyzer built against a " +
            "newer Roslyn than the one in-process)");
        _output.WriteLine($"{silent.Count} of {project.AnalyzerReferences.Count} references yield no C# analyzers");
    }

    [Fact]
    public async Task Assembly_Binding_Mismatch_Is_Detectable()
    {
        // Arrange
        using var loaded = await LoadRepositoryProjectAsync();
        var inProcess = typeof(Diagnostic).Assembly.GetName().Version;
        inProcess.ShouldNotBeNull();
        _output.WriteLine($"in-process Microsoft.CodeAnalysis: {inProcess}");

        var silentFiles = loaded.Project.AnalyzerReferences
            .Where(r => r.GetAnalyzers(LanguageNames.CSharp).IsEmpty && r.FullPath is not null)
            .ToList();
        silentFiles.ShouldNotBeEmpty("the silent references are file references with a path on disk");

        // Act — for each silent reference, the two facts a note needs: the assembly identity and
        // the Microsoft.CodeAnalysis version it binds. Both must be obtainable; neither is asserted
        // to a specific value, which would pin the test to one SDK.
        var bindings = new List<(AssemblyName Identity, Version? Binds)>();
        foreach (var reference in silentFiles)
        {
            var identity = AssemblyName.GetAssemblyName(reference.FullPath!);
            var binds = ReadReferencedRoslynVersion(reference.FullPath!);
            var verdict = binds is null ? "does not reference Microsoft.CodeAnalysis"
                : binds > inProcess ? $"binds Microsoft.CodeAnalysis {binds} — NEWER than in-process"
                : $"binds Microsoft.CodeAnalysis {binds}";
            _output.WriteLine($"{identity.Name} {identity.Version}: {verdict}");
            bindings.Add((identity, binds));
        }

        // Assert — the information is obtainable for every silent reference, and at least one of
        // them binds Microsoft.CodeAnalysis (so a version comparison against the in-process
        // assembly can be reported at all).
        bindings.ShouldAllBe(b => b.Identity.Name != null);
        bindings.ShouldContain(b => b.Binds != null,
            "a silent reference that binds Microsoft.CodeAnalysis is what a load-failure note names");
    }
}
