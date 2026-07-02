using RoselineMCP.Models;

namespace RoselineMCP.Interfaces;

/// <summary>
/// Interface for generating unified diff patches between text versions.
/// </summary>
public interface IPatchService
{
    /// <summary>
    /// Creates a unified diff patch between two text strings.
    /// </summary>
    /// <param name="before">The original text content.</param>
    /// <param name="after">The modified text content.</param>
    /// <param name="fileName">Optional file name for the patch header.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A response containing the patch and change statistics.</returns>
    CreatePatchResponse CreatePatch(
        string before,
        string after,
        string? fileName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a unified diff patch with additional options.
    /// </summary>
    /// <param name="before">The original text content.</param>
    /// <param name="after">The modified text content.</param>
    /// <param name="fileName">Optional file name for the patch header.</param>
    /// <param name="contextLines">Number of context lines to include.</param>
    /// <param name="ignoreWhitespace">Whether to ignore whitespace differences.</param>
    /// <param name="ignoreCase">Whether to ignore case differences.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A response containing the patch and change statistics.</returns>
    CreatePatchResponse CreatePatchWithOptions(
        string before,
        string after,
        string? fileName = null,
        int contextLines = 3,
        bool ignoreWhitespace = false,
        bool ignoreCase = false,
        CancellationToken cancellationToken = default);
}