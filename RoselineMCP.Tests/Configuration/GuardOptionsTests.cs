using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RoselineMCP.Configuration;
using Shouldly;

namespace RoselineMCP.Tests.Configuration;

/// <summary>
/// Binding tests for the three compile-guard switches — <c>Guard</c>, <c>GuardEndpoint</c> and
/// <c>GuardTimeout</c> — through the exact path <c>Program.cs</c> uses.
/// </summary>
/// <remarks>
/// Same contract as <see cref="RoselineMcpOptionsBindingTests"/>: the key spelling <em>is</em> the
/// public surface, so setting the POCO property directly would prove the behavior while leaving a
/// typo in the key green forever. <c>Guard</c> matters more than most: it is default-off, so a key
/// an operator cannot reach means a feature that can never be turned on.
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public class GuardOptionsTests
{
    private static RoselineMcpOptions Bind(Action<IConfigurationBuilder> configure)
    {
        var builder = new ConfigurationBuilder();
        configure(builder);

        var services = new ServiceCollection();
        services.Configure<RoselineMcpOptions>(builder.Build().GetSection("RoselineMCP"));
        return services.BuildServiceProvider().GetRequiredService<IOptions<RoselineMcpOptions>>().Value;
    }

    /// <summary>
    /// Neutralizes the whole <c>ROSELINE_</c> → <c>RoselineMCP:</c> namespace for the caller's scope.
    /// Deliberately not narrowed to one key — see the remarks on the sibling suite's copy (#141).
    /// </summary>
    private static ScopedEnvironmentNamespace ClearAmbientRoselineSection() =>
        ScopedEnvironmentNamespace.Clear("ROSELINE_", "RoselineMCP");

    [Fact]
    public void Guard_Defaults_To_Off()
    {
        // Default-off is the contract: the guard opens a local IPC endpoint, and this repo's grain
        // is read-only/inert until an operator opts in.
        Bind(_ => { }).Guard.ShouldBeFalse();
    }

    [Fact]
    public void GuardEndpoint_Defaults_To_Unset_So_The_Path_Is_Derived()
    {
        Bind(_ => { }).GuardEndpoint.ShouldBeNull();
    }

    [Fact]
    public void GuardTimeout_Defaults_To_Ten_Seconds()
    {
        // Deliberately NOT DefaultTimeout: that is an analysis budget, this bounds a hook the
        // harness will itself kill. Same separation-of-clocks argument as ConfirmDestructiveWritesTimeout.
        Bind(_ => { }).GuardTimeout.ShouldBe(10_000);
        Bind(_ => { }).DefaultTimeout.ShouldBe(120_000);
    }

    [Fact]
    public void Guard_Binds_From_The_RoselineMCP_Json_Section()
    {
        var options = Bind(b => b.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RoselineMCP:Guard"] = "true",
            ["RoselineMCP:GuardEndpoint"] = "/tmp/roseline-guard.sock",
            ["RoselineMCP:GuardTimeout"] = "2500",
        }));

        options.Guard.ShouldBeTrue();
        options.GuardEndpoint.ShouldBe("/tmp/roseline-guard.sock");
        options.GuardTimeout.ShouldBe(2500);

        // The neighbouring switches keep their defaults — the section is not clobbered.
        options.ConfirmDestructiveWrites.ShouldBeTrue();
        options.WorkspaceCache.ShouldBeTrue();
        options.RunAnalyzers.ShouldBeTrue();
    }

    [Fact]
    public void Guard_Binds_From_The_Documented_Environment_Variable()
    {
        // The exact spelling the docs give operators, including the double prefix:
        // ROSELINE_ (provider prefix, stripped) + RoselineMCP__ (the section).
        const string key = "ROSELINE_RoselineMCP__Guard";

        using var _ = ClearAmbientRoselineSection();

        // With the variable unset the default stands, so the assertion below is meaningful.
        Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_")).Guard.ShouldBeFalse();

        using (ScopedEnvironmentVariable.Set(key, "true"))
        {
            Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_")).Guard.ShouldBeTrue();
        }
    }

    [Fact]
    public void GuardTimeout_Binds_From_The_Documented_Environment_Variable()
    {
        const string key = "ROSELINE_RoselineMCP__GuardTimeout";

        using var _ = ClearAmbientRoselineSection();

        Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_")).GuardTimeout.ShouldBe(10_000);

        using (ScopedEnvironmentVariable.Set(key, "750"))
        {
            Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_")).GuardTimeout.ShouldBe(750);
        }
    }

    [Fact]
    public void An_Ambient_All_Caps_Export_Cannot_Change_What_These_Tests_See()
    {
        // The provider matches case-insensitively while a POSIX environment block does not, so a
        // per-key clear would leave this variable standing and the binder would still read it (#141).
        using var ambient = ScopedEnvironmentVariable.Set("ROSELINE_ROSELINEMCP__GUARD", "true");
        using var _ = ClearAmbientRoselineSection();

        Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_")).Guard.ShouldBeFalse();
    }

    [Fact]
    public void The_Shipped_Appsettings_Lists_Every_Guard_Switch()
    {
        // appsettings.json is copied next to the binary and is the file an operator edits for a
        // `dotnet tool` install. A switch missing from it is a discoverability bug — and for a
        // default-off feature, discoverability is the whole adoption story.
        var appsettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.Exists(appsettings).ShouldBeTrue($"expected the shipped appsettings.json at {appsettings}");

        Bind(b => b.AddJsonFile(appsettings)).Guard.ShouldBeFalse();

        var text = File.ReadAllText(appsettings);
        text.ShouldContain("\"Guard\"");
        text.ShouldContain("GuardEndpoint");
        text.ShouldContain("GuardTimeout");
    }
}
