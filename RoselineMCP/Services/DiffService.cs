using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using RoselineMCP.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace RoselineMCP.Services;

/// <summary>
/// Service for generating unified diff patches.
/// </summary>
public class DiffService : IDiffService
{
    private const int DefaultContextLines = 3;

    /// <inheritdoc/>
    public string GenerateUnifiedDiff(string oldText, string newText, string oldPath, string newPath)
    {
        var diffBuilder = new InlineDiffBuilder(new Differ());
        // The 2-arg BuildDiffModel overload defaults ignoreWhitespace to TRUE, which silently
        // drops whitespace-only changes (e.g. reindentation) from the diff. Internally generated
        // diffs must never ignore whitespace; callers that want that opt in via NormalizeWhitespace.
        var diff = diffBuilder.BuildDiffModel(oldText, newText, ignoreWhitespace: false);

        if (!diff.HasDifferences)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"--- {oldPath}");
        sb.AppendLine($"+++ {newPath}");

        var hunks = BuildDiffHunks(diff);

        foreach (var hunk in hunks)
        {
            AppendHunkToStringBuilder(sb, diff, hunk);
        }

        return sb.ToString();
    }

    /// <inheritdoc/>
    public string NormalizeWhitespace(string text)
    {
        var lines = text.Split('\n');
        var normalized = new List<string>();

        foreach (var line in lines)
        {
            // Trim trailing whitespace
            var trimmed = line.TrimEnd();

            // Replace multiple spaces with single space
            var collapsed = Regex.Replace(trimmed, @"\s+", " ");

            normalized.Add(collapsed);
        }

        return string.Join('\n', normalized);
    }

    private List<DiffHunk> BuildDiffHunks(DiffPaneModel diff)
    {
        var hunks = new List<DiffHunk>();
        DiffHunk? currentHunk = null;

        for (int i = 0; i < diff.Lines.Count; i++)
        {
            var line = diff.Lines[i];

            if (line.Type != ChangeType.Unchanged)
            {
                if (currentHunk == null || i > currentHunk.EndIndex + DefaultContextLines * 2)
                {
                    currentHunk = new DiffHunk
                    {
                        StartIndex = Math.Max(0, i - DefaultContextLines),
                        EndIndex = Math.Min(diff.Lines.Count - 1, i + DefaultContextLines)
                    };
                    hunks.Add(currentHunk);
                }
                else
                {
                    currentHunk.EndIndex = Math.Min(diff.Lines.Count - 1, i + DefaultContextLines);
                }
            }
        }

        return hunks;
    }

    private void AppendHunkToStringBuilder(StringBuilder sb, DiffPaneModel diff, DiffHunk hunk)
    {
        var hunkLines = diff.Lines.Skip(hunk.StartIndex).Take(hunk.EndIndex - hunk.StartIndex + 1).ToList();

        var oldStart = CountLinesBeforeIndex(diff.Lines, hunk.StartIndex, ChangeType.Inserted) + 1;
        var oldCount = hunkLines.Count(l => l.Type != ChangeType.Inserted);

        var newStart = CountLinesBeforeIndex(diff.Lines, hunk.StartIndex, ChangeType.Deleted) + 1;
        var newCount = hunkLines.Count(l => l.Type != ChangeType.Deleted);

        sb.AppendLine($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@");

        foreach (var line in hunkLines)
        {
            switch (line.Type)
            {
                case ChangeType.Unchanged:
                    sb.AppendLine($" {line.Text}");
                    break;
                case ChangeType.Deleted:
                    sb.AppendLine($"-{line.Text}");
                    break;
                case ChangeType.Inserted:
                    sb.AppendLine($"+{line.Text}");
                    break;
                case ChangeType.Modified:
                    // DiffPlex InlineDiffBuilder does not produce Modified in practice;
                    // this is defensive code for future compatibility.
                    sb.AppendLine($"-{line.Text}");
                    sb.AppendLine($"+{line.Text}");
                    break;
            }
        }
    }

    private int CountLinesBeforeIndex(List<DiffPiece> lines, int index, ChangeType excludeType)
    {
        return lines.Take(index).Count(l => l.Type != excludeType);
    }

    private class DiffHunk
    {
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
    }
}