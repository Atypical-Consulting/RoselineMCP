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
        var options = Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_")
            .AddInMemoryCollection(new Dictionary<string, string?>()));

        // Sanity: with the variable unset the default stands, so the assertion below is meaningful.
        options.ConfirmDestructiveWrites.ShouldBeTrue();

        Environment.SetEnvironmentVariable("ROSELINE_RoselineMCP__ConfirmDestructiveWrites", "false");
        try
        {
            Bind(b => b.AddEnvironmentVariables(prefix: "ROSELINE_"))
                .ConfirmDestructiveWrites.ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ROSELINE_RoselineMCP__ConfirmDestructiveWrites", null);
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
}
