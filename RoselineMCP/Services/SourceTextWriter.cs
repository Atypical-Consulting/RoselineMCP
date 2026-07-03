using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace RoselineMCP.Services;

/// <summary>
/// Writes a Roslyn <see cref="SourceText"/> back to disk using the encoding the file was
/// originally read with (<see cref="SourceText.Encoding"/>), so a UTF-8-with-BOM or UTF-16
/// source file is not silently re-encoded on save. Roslyn carries the on-disk encoding through
/// document transformations (code fixes, formatting, renames), so the text handed back for a
/// changed document still knows how its file was encoded. When the text has no encoding (e.g.
/// documents created in memory), falls back to UTF-8 without BOM — the same default
/// <c>File.WriteAllTextAsync(path, string)</c> used previously.
/// </summary>
internal static class SourceTextWriter
{
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <paramref name="text"/> to <paramref name="filePath"/>, replacing the file's contents.</summary>
    public static async Task WriteAsync(string filePath, SourceText text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var writer = new StreamWriter(filePath, append: false, text.Encoding ?? DefaultEncoding);
        await using (writer.ConfigureAwait(false))
        {
            text.Write(writer, cancellationToken);
        }
    }
}
