using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using Shouldly;

namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Binds <see cref="RoselineMcpOptions"/> the way <c>Program.cs</c> does — the <c>RoselineMCP</c>
/// configuration section, fed by JSON plus <c>ROSELINE_</c>-prefixed environment variables — and
/// asserts the switches actually arrive.
/// </summary>
/// <remarks>
/// These names are a public contract: the only way an operator can reach these switches is by
/// spelling the key exactly, in a file (<c>appsettings.json</c>, which for a <c>dnx</c>/NuGet-tool
/// install lives in the install directory, not the repo) or an environment variable. A test that
/// sets the POCO property directly proves the behavior but not the reachability — a typo in the
/// key, or a wrong section path, would leave every such test green while the switch silently did
/// nothing in production.
/// </remarks>
public class RoselineMcpOptionsBindingTests
{
    private static RoselineMcpOptions Bind(Action<IConfigurationBuilder> configure)
    {
        var builder = new ConfigurationBuilder();
        configure(builder);

        var services = new ServiceCollection();
        // Exactly the call Program.cs makes. The section name stays a literal here on purpose —
        // it is half of the contract under test, not a value to be kept in sync with a constant.
        services.Configure<RoselineMcpOptions>(builder.Build().GetSection("RoselineMCP"));
        return services.BuildServiceProvider().GetRequiredService<IOptions<RoselineMcpOptions>>().Value;
    }

    /// <summary>
    /// Neutralizes the whole <c>ROSELINE_</c> → <c>RoselineMCP:</c> namespace for the caller's scope,
    /// handing every variable it captures back on dispose.
    /// </summary>
    /// <remarks>
    /// Do not narrow this back to a single key. These tests read the real provider, whose prefix and
    /// section matching are case-<b>in</b>sensitive, while a POSIX environment block is
    /// case-<b>sensitive</b> — so an ambient <c>ROSELINE_ROSELINEMCP__CONFIRMDESTRUCTIVEWRITES</c> is
    /// a different variable that a per-key clear leaves standing and the binder still reads, which
    /// is exactly the red bar issue #141 reports.
    /// <see cref="An_Ambient_All_Caps_Export_Cannot_Change_What_These_Tests_See"/> is the test that
    /// goes red if this is narrowed.
    /// </remarks>
    private static ScopedEnvironmentNamespace ClearAmbientRoselineSection() =>
        ScopedEnvironmentNamespace.Clear("ROSELINE_", "RoselineMCP");

    [Fact]
    public void ConfirmDestructiveWrites_Defaults_To_True_When_Nothing_Is_Configured()
    {
        Bind(_ => { }).ConfirmDestructiveWrites.ShouldBeTrue();
    }

    [Fact]
    public void ConfirmDestructiveWrites_Binds_From_The_RoselineMCP_Json_Section()
    {
        var options = Bind(b => b.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RoselineMCP:ConfirmDestructiveWrites"] = "false",
        }));

        options.ConfirmDestructiveWrites.ShouldBeFalse();
        // The neighbouring switches keep their defaults — the section is not clobbered.
        options.RunAnalyzers.ShouldBeTrue();
        options.WorkspaceCache.ShouldBeTrue();
    }

    [Fact]
    public void ConfirmDestructiveWrites_Binds_From_The_Documented_Environment_Variable()
    {
        // The exact spelling README.md/CLAUDE.md tell operators to set, including the double
        // prefix: ROSELINE_ (provider prefix, stripped) + RoselineMCP__ (the section).
        const string key = "ROSELINE_RoselineMCP__ConfirmDestructiveWrites";

        using var _ = ClearAmbientRoselineSection();

        var options = Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_"));

        // With the variable unset the default stands, so the assertion below is meaningful.
        options.ConfirmDestructiveWrites.ShouldBeTrue();

        using (ScopedEnvironmentVariable.Set(key, "false"))
        {
            Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_"))
                .ConfirmDestructiveWrites.ShouldBeFalse();
        }
    }

    [Fact]
    public void The_Shipped_Appsettings_Lists_ConfirmDestructiveWrites()
    {
        // RoselineMCP/appsettings.json is copied next to the binary, so it sits beside the test
        // assembly too. It is the file an operator edits for a `dotnet tool` install, and it is
        // meant to list every switch — a missing key is a discoverability bug, not just cosmetics.
        var appsettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.Exists(appsettings).ShouldBeTrue($"expected the shipped appsettings.json at {appsettings}");

        Bind(b => b.AddJsonFile(appsettings)).ConfirmDestructiveWrites.ShouldBeTrue();
        File.ReadAllText(appsettings).ShouldContain("ConfirmDestructiveWrites");
    }

    [Fact]
    public void ConfirmDestructiveWritesTimeout_Defaults_To_Five_Minutes_When_Nothing_Is_Configured()
    {
        // The default is a behavior change in its own right: before it, an accepted-but-unanswered
        // confirmation blocked the tool call forever. Pin it so the bound cannot be lost silently.
        Bind(_ => { }).ConfirmDestructiveWritesTimeout.ShouldBe(300_000);
    }

    [Fact]
    public void ConfirmDestructiveWritesTimeout_Binds_From_The_RoselineMCP_Json_Section()
    {
        var options = Bind(b => b.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RoselineMCP:ConfirmDestructiveWritesTimeout"] = "1500",
        }));

        options.ConfirmDestructiveWritesTimeout.ShouldBe(1500);
        // The neighbouring switches keep their defaults — the section is not clobbered.
        options.ConfirmDestructiveWrites.ShouldBeTrue();
        options.DefaultTimeout.ShouldBe(120_000);
    }

    [Fact]
    public void ConfirmDestructiveWritesTimeout_Binds_From_The_Documented_Environment_Variable()
    {
        // The exact spelling README.md/CLAUDE.md tell operators to set, including the double
        // prefix: ROSELINE_ (provider prefix, stripped) + RoselineMCP__ (the section).
        const string key = "ROSELINE_RoselineMCP__ConfirmDestructiveWritesTimeout";

        using var _ = ClearAmbientRoselineSection();

        var options = Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_"));

        // With the variable unset the default stands, so the assertion below is meaningful.
        options.ConfirmDestructiveWritesTimeout.ShouldBe(300_000);

        using (ScopedEnvironmentVariable.Set(key, "0"))
        {
            // Zero is the documented escape hatch back to an unbounded wait, so it must survive
            // the round-trip as zero rather than being treated as "unset".
            Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_"))
                .ConfirmDestructiveWritesTimeout.ShouldBe(0);
        }
    }

    [Fact]
    public void An_Ambient_All_Caps_Export_Cannot_Change_What_These_Tests_See()
    {
        // The regression test for #141 itself, and the only one in the suite that goes red if the
        // two tests above narrow their scope back to a single documented key. Without it the fix is
        // unpinned: a clean CI runner exports no ROSELINE_ variables, so reverting
        // ClearAmbientRoselineSection to ScopedEnvironmentVariable.Set(key, null) leaves every other
        // test — including all four ScopedEnvironmentNamespace ones — green while the bug is fully
        // back. Issue #141's own reproduction step was manual and left no automated trace.
        //
        // On a case-sensitive environment block (Linux, macOS — both CI legs) these names are
        // different variables from the documented mixed-case ones, which is precisely why a per-key
        // clear misses them. On Windows they are the same variable, so this test is a tautology
        // there; that is the platform being structurally immune, not the test being weak.
        using var ambientWrites = ScopedEnvironmentVariable.Set(
            "ROSELINE_ROSELINEMCP__CONFIRMDESTRUCTIVEWRITES", "false");
        using var ambientTimeout = ScopedEnvironmentVariable.Set(
            "ROSELINE_ROSELINEMCP__CONFIRMDESTRUCTIVEWRITESTIMEOUT", "0");

        using var _ = ClearAmbientRoselineSection();

        var options = Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_"));

        // The same two pre-assertions the tests above depend on, under the ambient export that used
        // to defeat them.
        options.ConfirmDestructiveWrites.ShouldBeTrue();
        options.ConfirmDestructiveWritesTimeout.ShouldBe(300_000);
    }

    [Fact]
    public void The_Shipped_Appsettings_Lists_ConfirmDestructiveWritesTimeout()
    {
        var appsettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.Exists(appsettings).ShouldBeTrue($"expected the shipped appsettings.json at {appsettings}");

        Bind(b => b.AddJsonFile(appsettings)).ConfirmDestructiveWritesTimeout.ShouldBe(300_000);
        File.ReadAllText(appsettings).ShouldContain("ConfirmDestructiveWritesTimeout");
    }
}
