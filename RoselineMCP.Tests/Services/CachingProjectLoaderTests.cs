using FakeItEasy;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using RoselineMCP.Interfaces;
using RoselineMCP.Services;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <see cref="CachingProjectLoader"/> — the decorator that reuses the loaded workspace
/// across tool calls. Uses a fake inner <see cref="IProjectLoader"/> that builds in-memory
/// workspaces whose file paths point at a hermetic temp directory, so cache hits, fingerprint
/// invalidation (mtime/length/deleted/new files), the <c>WorkspaceCache=false</c> bypass, handle
/// ownership, and LRU eviction are all exercised without MSBuild.
/// </summary>
public class CachingProjectLoaderTests : IDisposable
{
    /// <summary>Mirrors <c>CachingProjectLoader.MaxEntries</c> (internal, not visible to tests).</summary>
    private const int MaxEntries = 4;

    private readonly string _root;

    public CachingProjectLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"RoselineCachingLoader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* ignored */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates <c>{root}/{name}/{name}.csproj</c> + <c>Widget.cs</c> on disk; returns the csproj path.</summary>
    private string CreateProjectOnDisk(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(dir, "Widget.cs"), "class Widget { }");
        return csproj;
    }

    private CachingProjectLoader CreateLoader(FakeInnerLoader inner, bool workspaceCache = true) =>
        new(
            inner,
            Options.Create(new RoselineMcpOptions { WorkspaceCache = workspaceCache }),
            A.Fake<ILogger<CachingProjectLoader>>(),
            project =>
            {
                var name = project ?? "App";
                return Path.Combine(_root, name, $"{name}.csproj");
            });

    [Fact]
    public async Task Second_Load_Is_A_Cache_Hit_Returning_The_Same_Solution()
    {
        CreateProjectOnDisk("App");
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner);

        using var first = await loader.LoadAsync("App", Ct);
        using var second = await loader.LoadAsync("App", Ct);

        inner.LoadCount.ShouldBe(1);
        ReferenceEquals(second.Solution, first.Solution).ShouldBeTrue();
        second.Project.Id.ShouldBe(first.Project.Id);
    }

    [Fact]
    public async Task Cached_Load_Reports_The_Same_ResolvedPath_As_The_Cold_Load()
    {
        CreateProjectOnDisk("Scratch");
        var slnPath = Path.Combine(_root, "Repo.sln");
        var csprojPath = Path.Combine(_root, "Scratch", "Scratch.csproj");
        var inner = new FakeInnerLoader(_root, solutionFilePath: slnPath, resolvedPathOverride: csprojPath);
        using var loader = CreateLoader(inner);

        using var first = await loader.LoadAsync("Scratch", Ct);
        using var second = await loader.LoadAsync("Scratch", Ct);

        inner.LoadCount.ShouldBe(1);
        first.ResolvedPath.ShouldBe(csprojPath);
        second.ResolvedPath.ShouldBe(
            csprojPath, "the cached (second) call must report the same resolvedPath as the cold load, not the derived .sln fallback");
    }

    [Fact]
    public async Task Aliases_Resolving_To_The_Same_Target_Share_One_Cache_Entry()
    {
        CreateProjectOnDisk("App");
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner);

        // null auto-discovers "App" in the fake resolver, so both spellings share the entry.
        using var byName = await loader.LoadAsync("App", Ct);
        using var byNull = await loader.LoadAsync(null, Ct);

        inner.LoadCount.ShouldBe(1);
        ReferenceEquals(byNull.Solution, byName.Solution).ShouldBeTrue();
    }

    [Fact]
    public async Task Touched_Document_Invalidates_The_Entry_And_Disposes_The_Stale_Workspace()
    {
        CreateProjectOnDisk("App");
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner);
        using var first = await loader.LoadAsync("App", Ct);

        // Bump the document's last-write-time (deterministic — no timestamp-granularity races).
        File.SetLastWriteTimeUtc(Path.Combine(_root, "App", "Widget.cs"), DateTime.UtcNow.AddMinutes(1));

        using var second = await loader.LoadAsync("App", Ct);

        inner.LoadCount.ShouldBe(2);
        ReferenceEquals(second.Solution, first.Solution).ShouldBeFalse();
        inner.Workspaces[0].Disposed.ShouldBeTrue();
        inner.Workspaces[1].Disposed.ShouldBeFalse();
    }

    [Fact]
    public async Task Deleted_Document_Invalidates_The_Entry()
    {
        CreateProjectOnDisk("App");
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner);
        using var first = await loader.LoadAsync("App", Ct);

        File.Delete(Path.Combine(_root, "App", "Widget.cs"));

        using var second = await loader.LoadAsync("App", Ct);

        inner.LoadCount.ShouldBe(2);
        inner.Workspaces[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task New_File_In_A_Tracked_Directory_Invalidates_The_Entry()
    {
        CreateProjectOnDisk("App");
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner);
        using var first = await loader.LoadAsync("App", Ct);

        // A brand-new file leaves every per-file stamp intact; only the directory stamp can catch
        // it. Creating the file updates the directory's mtime; bump it explicitly as well so the
        // test is immune to coarse filesystem timestamp granularity.
        var dir = Path.Combine(_root, "App");
        File.WriteAllText(Path.Combine(dir, "Brand.cs"), "class Brand { }");
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddMinutes(1));

        using var second = await loader.LoadAsync("App", Ct);

        inner.LoadCount.ShouldBe(2);
    }

    [Fact]
    public async Task WorkspaceCache_False_Bypasses_The_Cache_Entirely()
    {
        CreateProjectOnDisk("App");
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner, workspaceCache: false);

        var first = await loader.LoadAsync("App", Ct);
        first.Dispose();
        using var second = await loader.LoadAsync("App", Ct);

        inner.LoadCount.ShouldBe(2);
        // Pass-through handles keep the original owning behavior: disposing disposes.
        inner.Workspaces[0].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Disposing_A_Cached_Handle_Does_Not_Dispose_The_Shared_Workspace()
    {
        CreateProjectOnDisk("App");
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner);

        var first = await loader.LoadAsync("App", Ct);
        first.Dispose();

        inner.Workspaces[0].Disposed.ShouldBeFalse();

        // The cached workspace is still fully usable afterwards.
        using var second = await loader.LoadAsync("App", Ct);
        inner.LoadCount.ShouldBe(1);
        second.Project.Name.ShouldBe("App");
    }

    [Fact]
    public async Task Exceeding_The_Bound_Evicts_And_Disposes_The_Least_Recently_Used_Entry()
    {
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner);

        for (var i = 0; i <= MaxEntries; i++)
        {
            CreateProjectOnDisk($"App{i}");
            using var _ = await loader.LoadAsync($"App{i}", Ct);
        }

        inner.LoadCount.ShouldBe(MaxEntries + 1);
        inner.Workspaces[0].Disposed.ShouldBeTrue("the least-recently-used entry must be disposed on eviction");
        for (var i = 1; i <= MaxEntries; i++)
        {
            inner.Workspaces[i].Disposed.ShouldBeFalse($"App{i} should still be cached");
        }

        // The evicted target reloads (miss), the surviving ones stay hits.
        using var reloaded = await loader.LoadAsync("App0", Ct);
        inner.LoadCount.ShouldBe(MaxEntries + 2);
    }

    [Fact]
    public async Task Disposing_The_Loader_Disposes_Every_Cached_Workspace()
    {
        CreateProjectOnDisk("A");
        CreateProjectOnDisk("B");
        var inner = new FakeInnerLoader(_root);
        var loader = CreateLoader(inner);
        using (await loader.LoadAsync("A", Ct)) { }
        using (await loader.LoadAsync("B", Ct)) { }

        loader.Dispose();

        inner.Workspaces.ShouldAllBe(w => w.Disposed);
    }

    [Fact]
    public async Task Key_Resolution_Failure_Falls_Through_To_The_Inner_Loader_Uncached()
    {
        CreateProjectOnDisk("App");
        var inner = new FakeInnerLoader(_root);
        using var loader = new CachingProjectLoader(
            inner,
            Options.Create(new RoselineMcpOptions()),
            A.Fake<ILogger<CachingProjectLoader>>(),
            _ => throw new FileNotFoundException("cannot resolve"));

        using (await loader.LoadAsync("App", Ct)) { }
        using (await loader.LoadAsync("App", Ct)) { }

        // Never cached: the inner loader is consulted every time (and would surface its own error).
        inner.LoadCount.ShouldBe(2);
    }

    /// <summary>
    /// Fake inner loader: each call builds a fresh in-memory workspace for
    /// <c>{root}/{name}/{name}.csproj</c> with a single <c>Widget.cs</c> document, both pointing at
    /// real files on disk so the decorator's fingerprint has something to stat.
    /// </summary>
    private sealed class FakeInnerLoader : IProjectLoader
    {
        private readonly string _root;
        private readonly string? _solutionFilePath;
        private readonly string? _resolvedPathOverride;

        public FakeInnerLoader(string root, string? solutionFilePath = null, string? resolvedPathOverride = null)
        {
            _root = root;
            _solutionFilePath = solutionFilePath;
            _resolvedPathOverride = resolvedPathOverride;
        }

        public int LoadCount { get; private set; }

        public List<TrackedWorkspace> Workspaces { get; } = [];

        public Task<LoadedProject> LoadAsync(string? project, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            var name = project ?? "App";
            var csprojPath = Path.Combine(_root, name, $"{name}.csproj");
            var docPath = Path.Combine(_root, name, "Widget.cs");

            var workspace = new TrackedWorkspace();
            var projectId = ProjectId.CreateNewId();
            var documents = new List<DocumentInfo>();
            if (File.Exists(docPath))
            {
                documents.Add(DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(docPath),
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(File.ReadAllText(docPath)), VersionStamp.Create(), docPath)),
                    filePath: docPath));
            }

            var projectInfo = ProjectInfo.Create(
                projectId, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                filePath: csprojPath, documents: documents);
            var solutionInfo = SolutionInfo.Create(
                SolutionId.CreateNewId(), VersionStamp.Create(), _solutionFilePath, projects: [projectInfo]);
            var solution = workspace.AddSolution(solutionInfo);

            Workspaces.Add(workspace);
            return Task.FromResult(new LoadedProject(
                workspace, solution, solution.GetProject(projectId)!, resolvedPath: _resolvedPathOverride));
        }

        /// <summary>
        /// Never exercised here: the decorator resolves the owning project itself and then calls its
        /// own <c>LoadAsync</c>, precisely so a file-anchored load shares the cache. Reaching this
        /// would mean that routing had been changed to bypass the cache.
        /// </summary>
        public Task<LoadedProject?> LoadForFileAsync(string absoluteFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The caching decorator must not delegate LoadForFileAsync to the inner loader.");
    }

    /// <summary>
    /// Minimal in-memory <see cref="Workspace"/> that records disposal — <c>AdhocWorkspace</c> is
    /// sealed, so ownership/eviction disposal is observed via this subclass instead.
    /// </summary>
    private sealed class TrackedWorkspace : Workspace
    {
        public TrackedWorkspace()
            : base(MefHostServices.DefaultHost, "Custom")
        {
        }

        public bool Disposed { get; private set; }

        public Solution AddSolution(SolutionInfo solutionInfo)
        {
            OnSolutionAdded(solutionInfo);
            return CurrentSolution;
        }

        protected override void Dispose(bool finalize)
        {
            Disposed = true;
            base.Dispose(finalize);
        }
    }
}
