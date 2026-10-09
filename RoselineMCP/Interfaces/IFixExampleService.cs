using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Retrieves "key examples" for a diagnostic: sites in the loaded solution, in the same
/// syntactic/semantic shape as an occurrence, where the diagnostic does not fire. Read-only.
/// </summary>
public interface IFixExampleService
{
    /// <summary>Suggests fix examples for diagnostic <paramref name="id"/>.</summary>
    Task<FixExamplesResponse> SuggestAsync(
        string? project, string id, string? file, int? line,
        int maxExamples, int maxCandidates, CancellationToken cancellationToken = default);
}
