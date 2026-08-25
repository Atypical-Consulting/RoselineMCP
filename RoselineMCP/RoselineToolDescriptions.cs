namespace RoselineMCP;

/// <summary>
/// Reusable fragments composed into the <c>[Description]</c> of <c>[McpServerTool]</c> methods.
/// Only text that is genuinely shared belongs here: a per-tool limitation stays on its tool.
/// </summary>
/// <remarks>
/// <para>
/// Members are <c>const</c> because attribute arguments must be compile-time constants —
/// <c>[Description("… " + RoselineToolDescriptions.X)]</c> is legal, a <c>static readonly</c> field
/// is not.
/// </para>
/// <para>
/// The type is <c>public</c> rather than <c>internal</c> so
/// <c>ToolDescriptionContractTests</c> can assert the fragment <em>verbatim</em> against the very
/// constant the tools compose. This assembly exposes no <c>InternalsVisibleTo</c>, and the
/// established alternative here (see <c>ToolErrorContractTests</c>) is to re-declare the value in
/// the test — which would defeat the single-authority guarantee this constant exists to provide.
/// Every <c>[McpServerToolType]</c> class in this assembly is public for the same reason.
/// </para>
/// </remarks>
public static class RoselineToolDescriptions
{
    /// <summary>
    /// The one limitation shared by every tool whose <c>project</c> parameter is optional:
    /// discovery is anchored to the server's working directory, not the caller's. Kept in one place
    /// so <c>ToolDescriptionContractTests</c> can assert all twelve agree verbatim.
    /// </summary>
    /// <remarks>
    /// It carries no <c>Limitations:</c> label of its own: each tool's bespoke clause opens the
    /// label and this fragment continues that same sentence, so the twelve descriptions read as one
    /// paragraph instead of two consecutively-labelled ones.
    /// </remarks>
    public const string ProjectAutoDiscoveryLimit =
        " Auto-discovery uses the SERVER's working directory (fixed at spawn), not yours: in a git "
        + "worktree an omitted project silently resolves the MAIN checkout — check resolvedPath "
        + "(present on failures too) or pass an absolute .sln/.csproj path.";
}
