using FakeItEasy;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging;
using RoselineMCP.Services;
using RoselineMCP.Tests.Configuration;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// A broken workload install (MSB4242: "Workload set version … has missing manifests") fails the
/// SDK's workload resolver for every SDK-style project. MSBuildWorkspace reports that through
/// <c>WorkspaceFailed</c> and hands back projects with no documents, so every tool answered
/// "0 found" / "Symbol not found". The loader must fall back to
/// <c>MSBuildEnableWorkloadResolver=false</c> and load again.
/// </summary>
/// <remarks>
/// The breakage is simulated with a corrupt manifest under <c>DOTNETSDK_WORKLOAD_MANIFEST_ROOTS</c>,
/// which the build host inherits — hence the process-environment collection.
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WorkloadResolverFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RoselineWorkload_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { /* ignored */ }
    }

    [Fact]
    public async Task A_Broken_Workload_Resolver_Still_Loads_The_Projects_Documents()
    {
        var msBuild = new MSBuildService(A.Fake<ILogger<MSBuildService>>());
        var manifestRoot = Path.Combine(_root, "manifests");
        foreach (var version in MSBuildLocator.QueryVisualStudioInstances().Select(i => i.Version))
        {
            var manifestDir = Path.Combine(manifestRoot, $"{version.Major}.{version.Minor}.{version.Build / 100 * 100}", "broken.manifest");
            Directory.CreateDirectory(manifestDir);
            File.WriteAllText(Path.Combine(manifestDir, "WorkloadManifest.json"), "not json");
        }

        var dir = Path.Combine(_root, "App");
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "App.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "A.cs"), "class A { }");

        using var env = ScopedEnvironmentVariable.Set("DOTNETSDK_WORKLOAD_MANIFEST_ROOTS", manifestRoot);
        var loader = new ProjectLoader(A.Fake<ILogger<ProjectLoader>>(), msBuild);
        using var loaded = await loader.LoadAsync(csproj);

        msBuild.WorkloadResolverDisabled.ShouldBeTrue("the corrupt manifest should have failed the workload resolver");
        loaded.Project.Documents.Select(d => d.Name).ShouldContain("A.cs");
    }
}
