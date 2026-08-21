using System.Text.Json;
using RoselineMCP.Guard;
using Shouldly;

namespace RoselineMCP.Tests.Services;

/// <summary>
/// Tests for <c>roseline-mcp guard --print-hook</c> — the one-command installer output an operator
/// pastes into <c>settings.json</c>.
/// </summary>
/// <remarks>
/// RoselineMCP prints the block; it never edits anyone's <c>settings.json</c> itself. Wiring a hook
/// changes what runs after every tool call in that repository, and a tool that quietly rewrote its
/// own host's configuration to install itself would be doing something the operator did not ask for.
/// </remarks>
public class GuardPrintHookTests
{
    private static JsonElement PrintAndParse()
    {
        var stdout = new StringWriter();

        GuardClient.PrintHook(stdout).ShouldBe(0);

        var text = stdout.ToString();
        text.ShouldNotBeNullOrWhiteSpace();

        // It is pasted into a JSON file, so it has to BE JSON — not merely look like it.
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public void The_Printed_Block_Is_Valid_Json()
    {
        PrintAndParse().ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void It_Hooks_PostToolUse_On_Every_File_Writing_Tool()
    {
        var entry = PrintAndParse()
            .GetProperty("hooks")
            .GetProperty("PostToolUse")[0];

        // The whole point of the feature: fire whatever tool did the writing, not only RoselineMCP's.
        entry.GetProperty("matcher").GetString().ShouldBe("Edit|Write|MultiEdit");
    }

    [Fact]
    public void The_Command_Ends_With_The_Guard_Verb()
    {
        var hook = PrintAndParse()
            .GetProperty("hooks")
            .GetProperty("PostToolUse")[0]
            .GetProperty("hooks")[0];

        hook.GetProperty("type").GetString().ShouldBe("command");
        hook.GetProperty("command").GetString().ShouldNotBeNullOrWhiteSpace();
        hook.GetProperty("command").GetString().ShouldEndWith("guard");
    }

    /// <summary>
    /// An <c>apphost</c> launch — a <c>dotnet tool</c> install — runs the executable directly.
    /// </summary>
    [Fact]
    public void An_Apphost_Launch_Prints_The_Executable_Itself()
    {
        GuardClient.BuildHookCommand("/usr/local/bin/roseline-mcp", "/opt/roseline/RoselineMCP.dll")
            .ShouldBe("\"/usr/local/bin/roseline-mcp\" guard");
    }

    /// <summary>
    /// A framework-dependent launch goes through the <c>dotnet</c> muxer, where
    /// <c>Environment.ProcessPath</c> is <c>dotnet</c> itself. Printing that alone yields
    /// <c>dotnet guard</c> — a command that is not this program at all. This is a regression guard
    /// for a defect that shipped and that a tautological assertion had declared covered.
    /// </summary>
    [Fact]
    public void A_Muxer_Launch_Prints_Dotnet_Plus_The_Entry_Assembly()
    {
        GuardClient.BuildHookCommand("/opt/homebrew/bin/dotnet", "/opt/roseline/RoselineMCP.dll")
            .ShouldBe("\"/opt/homebrew/bin/dotnet\" \"/opt/roseline/RoselineMCP.dll\" guard");
    }

    [Fact]
    public void Paths_Are_Quoted_So_Spaces_Survive()
    {
        // "C:\Program Files\..." and "/Users/x/My Tools/..." are the common case, not the exotic one.
        GuardClient.BuildHookCommand("/Users/x/My Tools/roseline-mcp", null)
            .ShouldBe("\"/Users/x/My Tools/roseline-mcp\" guard");
    }

    [Fact]
    public void An_Unknown_Process_Path_Falls_Back_To_The_Tool_Name()
    {
        GuardClient.BuildHookCommand(null, null).ShouldBe("roseline-mcp guard");
    }

    [Fact]
    public void The_Hook_Carries_A_Timeout()
    {
        var hook = PrintAndParse()
            .GetProperty("hooks")
            .GetProperty("PostToolUse")[0]
            .GetProperty("hooks")[0];

        // The harness default is 600 s. A guard that could stall an agent's turn for ten minutes
        // would be removed the first time a workspace load went slow.
        hook.TryGetProperty("timeout", out var timeout).ShouldBeTrue();
        timeout.GetInt32().ShouldBeGreaterThan(0);
        timeout.GetInt32().ShouldBeLessThanOrEqualTo(120);
    }
}
