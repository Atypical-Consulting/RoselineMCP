using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// The parts of a write tool's response that the shared verified-write flow needs to read and set:
/// the compiler's verdict, whether anything was applied, and the preview flag and notes it may have
/// to downgrade and explain.
/// </summary>
/// <remarks>
/// It exists so the flow can be written <b>once</b>. The three write tools already grew three
/// divergent copies of the confirmation block — one of which asked a human to approve a write
/// "in ''" — and that block is now a shared helper for exactly this reason. Verification adds a
/// second, longer sequence (verify → ask → write) around the same three tools; giving their
/// responses one shape is what keeps it from becoming the next set of three copies.
/// </remarks>
public interface IWriteToolResponse
{
    /// <summary>Whether the call ended as a preview. The flow sets it when a confirmation is declined or expires.</summary>
    bool PreviewOnly { get; set; }

    /// <summary>Whether changes actually reached disk.</summary>
    bool Applied { get; set; }

    /// <summary>The compiler's verdict on the change, or null when no verification was performed.</summary>
    VerificationVerdict? Verification { get; set; }

    /// <summary>Human-readable notes; the flow appends the confirmation outcome here.</summary>
    List<string> Notes { get; }

    /// <summary>
    /// Whether this response carries anything to write. Derived from the payload's own changed-file
    /// collection, never a flag a tool sets independently, so no tool can disagree with the flow
    /// about what "no changes" means.
    /// </summary>
    bool HasChanges { get; }
}
