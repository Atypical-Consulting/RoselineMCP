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

        // Some fixtures point a fake solution path directly at _root (e.g. "{root}/Repo.sln"), which
        // makes _root itself a tracked directory even though that .sln never exists on disk. Backdate
        // it here too, for the same reason CreateProjectOnDisk backdates what it creates: a freshly
        // created directory would otherwise be flagged racy (issue #235) at the very first capture.
        Directory.SetLastWriteTimeUtc(_root, DateTime.UtcNow.AddSeconds(-10));
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_root, true); }
        catch { /* ignored */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Creates <c>{root}/{name}/{name}.csproj</c> + <c>Widget.cs</c> on disk; returns the csproj path.
    /// Backdates every created file/directory's <c>LastWriteTimeUtc</c> well past
    /// <c>CachingProjectLoader</c>'s racy window (issue #235), so an ordinary cache-hit test's fixture
    /// is never itself flagged racy at capture — that's what the deliberately fresh writes in the
    /// stamp-collision regression tests are for.
    /// </summary>
    private string CreateProjectOnDisk(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, $"{name}.csproj");
        var widget = Path.Combine(dir, "Widget.cs");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(widget, "class Widget { }");

        var settled = DateTime.UtcNow.AddSeconds(-10);
        File.SetLastWriteTimeUtc(csproj, settled);
        File.SetLastWriteTimeUtc(widget, settled);
        Directory.SetLastWriteTimeUtc(dir, settled);
        // Directory.CreateDirectory(dir) just bumped _root's own mtime (a new child was added);
        // re-settle it too, since some fixtures point a fake .sln straight at _root (see the
        // constructor's own comment).
        Directory.SetLastWriteTimeUtc(_root, settled);

        return csproj;
    }

    private CachingProjectLoader CreateLoader(FakeInnerLoader inner, bool workspaceCache = true, Func<DateTime>? utcNowProvider = null) =>
        new(
            inner,
            Options.Create(new RoselineMcpOptions { WorkspaceCache = workspaceCache }),
            A.Fake<ILogger<CachingProjectLoader>>(),
            project =>
            {
                var name = project ?? "App";
                return Path.Combine(_root, name, $"{name}.csproj");
            },
            utcNowProvider);

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

    /// <summary>
    /// Issue #235 / prior art #233: <c>(LastWriteTimeUtc, Length)</c> alone cannot distinguish a
    /// same-length edit whose new timestamp rounds into the same filesystem-granularity bucket as
    /// the one captured for the cache entry. Widget.cs is overwritten immediately before the first
    /// load — its mtime is therefore still fresh (racy) relative to that load's own capture — then a
    /// same-length rewrite is forced to collide with the exact captured stamp via
    /// <see cref="File.SetLastWriteTimeUtc(string, DateTime)"/>, deterministically reproducing the
    /// collision rather than racing the real OS clock. Pre-fix, <c>IsCurrent()</c> trusts the bare
    /// stat match and this test fails (<c>inner.LoadCount</c> stays 1); the fix must force a reload
    /// whenever the fingerprint was captured while a tracked file's write was still fresh.
    /// </summary>
    [Fact]
    public async Task Same_Length_Edit_Colliding_With_The_Captured_Stamp_Invalidates()
    {
        CreateProjectOnDisk("App");
        var widgetPath = Path.Combine(_root, "App", "Widget.cs");
        File.WriteAllText(widgetPath, "class Widget { int A() => 1; }"); // freshly written -> racy at capture
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner);

        using var first = await loader.LoadAsync("App", Ct);
        var capturedStamp = File.GetLastWriteTimeUtc(widgetPath);

        // Different content, same byte length, then force the mtime back to the exact captured
        // value — a deterministic stand-in for two writes landing in the same OS timestamp tick.
        File.WriteAllText(widgetPath, "class Widget { int A() => 2; }");
        File.SetLastWriteTimeUtc(widgetPath, capturedStamp);
        File.GetLastWriteTimeUtc(widgetPath).ShouldBe(capturedStamp, "the forced collision must actually be observed back");

        using var second = await loader.LoadAsync("App", Ct);

        inner.LoadCount.ShouldBe(2, "a fingerprint captured while Widget.cs was still fresh must never be trusted on a bare stat match");
        ReferenceEquals(second.Solution, first.Solution).ShouldBeFalse();
    }

    /// <summary>
    /// Distinguishes the *correct* predicate (raciness decided once, at capture, from the tracked
    /// file's own mtime against the capture timestamp) from an *incorrect* one an earlier design
    /// draft used (trusting a stat match once enough wall-clock time has passed since capture,
    /// regardless of how fresh the captured mtime itself was). Simulates "the next check arrives long
    /// after capture" via the injectable clock rather than a real delay — under the incorrect,
    /// check-time-relative predicate this collision would be (wrongly) trusted once <c>now -
    /// CapturedAtUtc</c> exceeds the racy window; under the correct, capture-time-relative one, how
    /// long the check waits is irrelevant, because the flag was already decided at capture.
    /// </summary>
    [Fact]
    public async Task Same_Length_Edit_Colliding_With_The_Captured_Stamp_Invalidates_Even_When_The_Next_Check_Is_Much_Later()
    {
        CreateProjectOnDisk("App");
        var widgetPath = Path.Combine(_root, "App", "Widget.cs");
        File.WriteAllText(widgetPath, "class Widget { int A() => 1; }");
        var writeStamp = File.GetLastWriteTimeUtc(widgetPath);

        var fakeNow = writeStamp; // capture happens essentially at the write itself -> maximally racy
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner, utcNowProvider: () => fakeNow);

        using var first = await loader.LoadAsync("App", Ct);

        File.WriteAllText(widgetPath, "class Widget { int A() => 2; }");
        File.SetLastWriteTimeUtc(widgetPath, writeStamp);
        File.GetLastWriteTimeUtc(widgetPath).ShouldBe(writeStamp, "the forced collision must actually be observed back");

        // Simulate the second check arriving 5 minutes later — far past any reasonable
        // check-time-relative racy window — with no real delay.
        fakeNow = writeStamp.AddMinutes(5);

        using var second = await loader.LoadAsync("App", Ct);

        inner.LoadCount.ShouldBe(2, "raciness is decided once at capture; a later check must not start trusting an already-racy fingerprint");
        ReferenceEquals(second.Solution, first.Solution).ShouldBeFalse();
    }

    /// <summary>
    /// Proves the fix does not merely bound the reload cascade — it eliminates it for the case that
    /// matters most: <c>rename_symbol</c> writes, then an immediate follow-up call
    /// (<c>check_compilation</c>, <c>list_diagnostics</c>, ...) is the exact agent sequence this
    /// server exists to serve. Every load below happens WITHOUT ever advancing the injected clock —
    /// so every one of them lands strictly inside <c>RacyWindow</c> of Widget.cs's own last write, the
    /// scenario the code review on this issue found was silently untested (the other tests all
    /// fast-forward the clock between loads). If a racy capture forced a reload on every subsequent
    /// check regardless of content, this would assert <c>LoadCount == 4</c>; the content-hash
    /// fallback means only the genuine content change (write 2) pays a reload — the rest are cache
    /// hits, with no cascade at all, however soon they land inside the window.
    /// </summary>
    [Fact]
    public async Task Consecutive_Calls_Inside_The_Racy_Window_Do_Not_Each_Force_A_Reload()
    {
        CreateProjectOnDisk("App");
        var widgetPath = Path.Combine(_root, "App", "Widget.cs");
        File.WriteAllText(widgetPath, "class Widget { int A() => 1; }");
        var fakeNow = File.GetLastWriteTimeUtc(widgetPath); // capture happens right at the write -> racy
        var inner = new FakeInnerLoader(_root);
        using var loader = CreateLoader(inner, utcNowProvider: () => fakeNow);

        // Load 1: cold load. Its own capture is racy (Widget.cs was just written).
        using var first = await loader.LoadAsync("App", Ct);
        inner.LoadCount.ShouldBe(1);

        // Loads 2 and 3: nothing on disk has changed, the clock has NOT advanced, and the cached
        // fingerprint is still racy — this is exactly the case a forced-reload-while-racy design
        // would cascade on. Both must be cache hits.
        using var second = await loader.LoadAsync("App", Ct);
        inner.LoadCount.ShouldBe(1, "unchanged content under a racy fingerprint must be a cache hit, not a forced reload");
        ReferenceEquals(second.Solution, first.Solution).ShouldBeTrue();

        using var third = await loader.LoadAsync("App", Ct);
        inner.LoadCount.ShouldBe(1, "a second consecutive call inside the same racy window must not cascade into another reload either");
        ReferenceEquals(third.Solution, first.Solution).ShouldBeTrue();

        // Now a genuine content change, same length, mtime forced back to collide — still with no
        // clock advance at all. This is the one call that must reload.
        File.WriteAllText(widgetPath, "class Widget { int A() => 2; }");
        File.SetLastWriteTimeUtc(widgetPath, fakeNow);

        using var fourth = await loader.LoadAsync("App", Ct);
        inner.LoadCount.ShouldBe(2, "a real content change colliding with the captured stamp must still be caught and reloaded exactly once");
        ReferenceEquals(fourth.Solution, first.Solution).ShouldBeFalse();

        // And the call right after THAT reload, still inside the window, still with no clock
        // advance, must again be a hit rather than cascading a second time.
        using var fifth = await loader.LoadAsync("App", Ct);
        inner.LoadCount.ShouldBe(2, "the reload that just happened must not itself cascade into a further reload");
        ReferenceEquals(fifth.Solution, fourth.Solution).ShouldBeTrue();
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
        using (await loader.LoadAsync("A", Ct))
        { }
        using (await loader.LoadAsync("B", Ct))
        { }

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

        using (await loader.LoadAsync("App", Ct))
        { }
        using (await loader.LoadAsync("App", Ct))
        { }

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
