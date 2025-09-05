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
    /// <returns>A response containing the patch and change statistics.</returns>
    CreatePatchResponse CreatePatch(string before, string after, string? fileName = null);

    /// <summary>
    /// Creates a unified diff patch from two file paths.
    /// </summary>
    /// <param name="beforePath">Path to the original file.</param>
    /// <param name="afterPath">Path to the modified file.</param>
    /// <returns>A response containing the patch and change statistics.</returns>
    CreatePatchResponse CreatePatchFromFiles(string beforePath, string afterPath);

    /// <summary>
    /// Creates a unified diff patch with additional options.
    /// </summary>
    /// <param name="before">The original text content.</param>
    /// <param name="after">The modified text content.</param>
    /// <param name="fileName">Optional file name for the patch header.</param>
    /// <param name="contextLines">Number of context lines to include.</param>
    /// <param name="ignoreWhitespace">Whether to ignore whitespace differences.</param>
    /// <param name="ignoreCase">Whether to ignore case differences.</param>
    /// <returns>A response containing the patch and change statistics.</returns>
    CreatePatchResponse CreatePatchWithOptions(
        string before,
        string after,
        string? fileName = null,
        int contextLines = 3,
        bool ignoreWhitespace = false,
        bool ignoreCase = false);

    /// <summary>
    /// Applies a unified diff patch to a file.
    /// </summary>
    /// <param name="filePath">Path to the file to patch.</param>
    /// <param name="patch">The unified diff patch to apply.</param>
    /// <returns>True if the patch was applied successfully, false otherwise.</returns>
    bool ApplyPatch(string filePath, string patch);
}