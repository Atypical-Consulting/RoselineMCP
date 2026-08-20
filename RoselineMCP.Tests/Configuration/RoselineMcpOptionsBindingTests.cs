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
        // Exactly the call Program.cs makes.
        services.Configure<RoselineMcpOptions>(builder.Build().GetSection("RoselineMCP"));
        return services.BuildServiceProvider().GetRequiredService<IOptions<RoselineMcpOptions>>().Value;
    }

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

        // Neutralizes the WHOLE ROSELINE_ → RoselineMCP: namespace for the scope, not just this one
        // key, and hands every variable it captures back afterwards. Do not narrow it back: this
        // test reads the real provider, whose prefix and section matching are case-INsensitive,
        // while a POSIX environment block is case-sensitive — so an ambient
        // ROSELINE_ROSELINEMCP__CONFIRMDESTRUCTIVEWRITES is a different variable that a per-key
        // clear leaves standing and the binder below still reads (#141).
        using var _ = ScopedEnvironmentNamespace.Clear("ROSELINE_", "RoselineMCP");

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

        // Neutralizes the WHOLE ROSELINE_ → RoselineMCP: namespace for the scope, not just this one
        // key, and hands every variable it captures back afterwards. Do not narrow it back: this
        // test reads the real provider, whose prefix and section matching are case-INsensitive,
        // while a POSIX environment block is case-sensitive — so an ambient
        // ROSELINE_ROSELINEMCP__CONFIRMDESTRUCTIVEWRITESTIMEOUT is a different variable that a
        // per-key clear leaves standing and the binder below still reads (#141).
        using var _ = ScopedEnvironmentNamespace.Clear("ROSELINE_", "RoselineMCP");

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
    public void The_Shipped_Appsettings_Lists_ConfirmDestructiveWritesTimeout()
    {
        var appsettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.Exists(appsettings).ShouldBeTrue($"expected the shipped appsettings.json at {appsettings}");

        Bind(b => b.AddJsonFile(appsettings)).ConfirmDestructiveWritesTimeout.ShouldBe(300_000);
        File.ReadAllText(appsettings).ShouldContain("ConfirmDestructiveWritesTimeout");
    }
}
