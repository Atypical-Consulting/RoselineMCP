using System.Text.Json.Serialization;

namespace RoselineMCP.Models;

/// <summary>
/// The uniform, typed envelope every MCP tool returns. Returning a concrete type (rather than a
/// hand-serialized JSON <see cref="string"/>) lets the MCP SDK advertise a generated
/// <c>outputSchema</c> to clients and emit <c>structuredContent</c> alongside the text block, so
/// callers can bind to fields instead of re-parsing an opaque blob.
/// </summary>
/// <remarks>
/// The envelope preserves the project's "never throw to the MCP layer" contract: a failure is an
/// ordinary, successful tool call whose payload has <see cref="Ok"/> = <see langword="false"/> and
/// a populated <see cref="Error"/> (the MCP-protocol <c>isError</c> flag stays false — the error is
/// reported in-band, exactly as the previous string responses did). On success <see cref="Data"/>
/// is present and <see cref="Error"/> is omitted; on failure the reverse.
/// </remarks>
/// <typeparam name="T">The tool's success payload type (e.g. <see cref="AnalyzeSolutionResponse"/>).</typeparam>
public sealed class ToolResult<T>
{
    /// <summary>Whether the tool succeeded. When false, inspect <see cref="Error"/>.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>The success payload; omitted (null) when <see cref="Ok"/> is false.</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public T? Data { get; init; }

    /// <summary>The failure detail; omitted (null) when <see cref="Ok"/> is true.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolError? Error { get; init; }

    /// <summary>Builds a successful result wrapping <paramref name="data"/>.</summary>
    public static ToolResult<T> Success(T data) => new() { Ok = true, Data = data };

    /// <summary>Builds a failed result carrying <paramref name="error"/>.</summary>
    public static ToolResult<T> Failure(ToolError error) => new() { Ok = false, Error = error };
}

/// <summary>
/// The machine-readable failure detail carried by a failed <see cref="ToolResult{T}"/>. The
/// <see cref="Type"/> is always one of the closed <c>ToolErrorTypes</c> set so callers can branch
/// on a documented contract rather than a raw CLR exception name.
/// </summary>
public sealed class ToolError
{
    /// <summary>Stable, machine-readable error category (see <c>ToolErrorTypes</c>).</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Human-readable message. Never contains raw exception text for internal errors.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional corrective hint (e.g. the accepted values, or which tool to call first).</summary>
    [JsonPropertyName("hint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }

    /// <summary>Per-invocation correlation ID tying this response to the full server-side log entry.</summary>
    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>
    /// The absolute <c>.sln</c>/<c>.csproj</c> that answered this call — the same value the success
    /// responses carry, so a caller can tell "the symbol is not there" apart from "I was answered
    /// from a different checkout" (two checkouts of one repository are otherwise reported
    /// identically: same project name, same solution-root-relative paths).
    /// </summary>
    /// <remarks>
    /// <b>Absent is meaningful.</b> The field is omitted — never <c>""</c> — when the failure
    /// happened before any project was resolved (a caller-input validation error, a load that
    /// itself failed). "Never resolved" is a different claim from "resolved to nothing", so the
    /// two are never conflated on the wire.
    /// </remarks>
    [JsonPropertyName("resolvedPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolvedPath { get; init; }
}
