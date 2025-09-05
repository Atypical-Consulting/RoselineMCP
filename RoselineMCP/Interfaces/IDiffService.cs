namespace RoselineMCP.Interfaces;

/// <summary>
/// Service for generating unified diff patches.
/// </summary>
public interface IDiffService
{
    /// <summary>
    /// Generates a unified diff between two text strings.
    /// </summary>
    /// <param name="oldText">The original text.</param>
    /// <param name="newText">The new text.</param>
    /// <param name="oldPath">The path for the original file in the diff header.</param>
    /// <param name="newPath">The path for the new file in the diff header.</param>
    /// <returns>A unified diff string.</returns>
    string GenerateUnifiedDiff(string oldText, string newText, string oldPath, string newPath);

    /// <summary>
    /// Normalizes whitespace in text for comparison.
    /// </summary>
    /// <param name="text">The text to normalize.</param>
    /// <returns>Text with normalized whitespace.</returns>
    string NormalizeWhitespace(string text);
}