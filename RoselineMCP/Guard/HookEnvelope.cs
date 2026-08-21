using System.Text.Json.Serialization;

namespace RoselineMCP.Guard;

/// <summary>
/// The subset of the agent harness's <c>PostToolUse</c> hook payload that the compile guard reads.
/// </summary>
/// <remarks>
/// Deliberately partial. The harness sends more than this and will send more still as it evolves;
/// binding only the four fields the guard acts on means a new field can never break the client, and
/// an absent one degrades to the silent path rather than to an exception.
/// </remarks>
public sealed class HookEnvelope
{
    /// <summary>Which hook fired. Anything other than <c>PostToolUse</c> is ignored.</summary>
    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; set; }

    /// <summary>The tool that ran — <c>Edit</c>, <c>Write</c>, <c>MultiEdit</c>, …</summary>
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; set; }

    /// <summary>
    /// The <b>agent's</b> working directory, not the server's. Captured for diagnostics only: the
    /// guard never resolves anything against it, because that divergence is exactly the bug it
    /// exists to avoid.
    /// </summary>
    [JsonPropertyName("cwd")]
    public string? Cwd { get; set; }

    /// <summary>The tool's arguments; only <c>file_path</c> is read.</summary>
    [JsonPropertyName("tool_input")]
    public HookToolInput? ToolInput { get; set; }
}

/// <summary>The one tool argument the compile guard needs.</summary>
public sealed class HookToolInput
{
    /// <summary>Absolute path of the file the tool wrote.</summary>
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }
}
