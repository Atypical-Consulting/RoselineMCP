using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Extensions.Logging;
using RoselineMCP.Models;
using System.Text;

namespace RoselineMCP.Services;

public class PatchService : IPatchService
{
    private readonly ILogger<PatchService> _logger;

    public PatchService(ILogger<PatchService> logger)
    {
        _logger = logger;
    }

    public CreatePatchResponse CreatePatch(string before, string after, string? fileName = null)
    {
        try
        {
            var response = new CreatePatchResponse
            {
                FileName = fileName ?? "file.txt"
            };

            // Generate the diff
            var diffBuilder = new InlineDiffBuilder(new Differ());
            var diff = diffBuilder.BuildDiffModel(before, after);

            response.HasChanges = diff.HasDifferences;

            if (!diff.HasDifferences)
            {
                response.Summary = "No changes detected";
                return response;
            }

            // Calculate statistics
            var linesAdded = 0;
            var linesRemoved = 0;
            var linesModified = 0;

            foreach (var line in diff.Lines)
            {
                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        linesAdded++;
                        break;
                    case ChangeType.Deleted:
                        linesRemoved++;
                        break;
                    case ChangeType.Modified:
                        linesModified++;
                        break;
                }
            }

            response.LinesAdded = linesAdded;
            response.LinesRemoved = linesRemoved;

            // Generate unified diff
            response.Patch = GenerateUnifiedDiff(
                before,
                after,
                $"a/{response.FileName}",
                $"b/{response.FileName}",
                diff);

            // Create summary
            var summaryParts = new List<string>();
            if (linesAdded > 0) summaryParts.Add($"+{linesAdded}");
            if (linesRemoved > 0) summaryParts.Add($"-{linesRemoved}");
            if (linesModified > 0) summaryParts.Add($"~{linesModified}");

            response.Summary = $"{response.FileName}: {string.Join(", ", summaryParts)} lines";

            _logger.LogInformation("Created patch for {FileName}: +{Added} -{Removed} lines",
                response.FileName, linesAdded, linesRemoved);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create patch");
            throw;
        }
    }

    private string GenerateUnifiedDiff(
        string oldText,
        string newText,
        string oldPath,
        string newPath,
        DiffPaneModel diff)
    {
        var sb = new StringBuilder();

        // Add header
        sb.AppendLine($"--- {oldPath}");
        sb.AppendLine($"+++ {newPath}");

        // Split into lines for processing
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');

        // Group changes into hunks
        int contextLines = 3;
        var hunks = new List<DiffHunk>();
        DiffHunk? currentHunk = null;

        for (int i = 0; i < diff.Lines.Count; i++)
        {
            var line = diff.Lines[i];

            if (line.Type != ChangeType.Unchanged)
            {
                // Start a new hunk or extend current one
                if (currentHunk == null || i > currentHunk.EndIndex + contextLines * 2)
                {
                    currentHunk = new DiffHunk
                    {
                        StartIndex = Math.Max(0, i - contextLines),
                        EndIndex = Math.Min(diff.Lines.Count - 1, i + contextLines)
                    };
                    hunks.Add(currentHunk);
                }
                else
                {
                    currentHunk.EndIndex = Math.Min(diff.Lines.Count - 1, i + contextLines);
                }
            }
        }

        // Generate output for each hunk
        foreach (var hunk in hunks)
        {
            // Calculate hunk header
            var hunkLines = diff.Lines.Skip(hunk.StartIndex).Take(hunk.EndIndex - hunk.StartIndex + 1).ToList();

            var oldStart = CountLinesBeforeIndex(diff.Lines, hunk.StartIndex, ChangeType.Inserted) + 1;
            var oldCount = hunkLines.Count(l => l.Type != ChangeType.Inserted);

            var newStart = CountLinesBeforeIndex(diff.Lines, hunk.StartIndex, ChangeType.Deleted) + 1;
            var newCount = hunkLines.Count(l => l.Type != ChangeType.Deleted);

            // Add hunk header
            sb.AppendLine($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@");

            // Add hunk content
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
                        // Modified lines are typically shown as delete + insert
                        sb.AppendLine($"-{line.Text}");
                        sb.AppendLine($"+{line.Text}");
                        break;
                }
            }
        }

        return sb.ToString();
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

    public CreatePatchResponse CreatePatchFromFiles(string beforePath, string afterPath)
    {
        try
        {
            if (!File.Exists(beforePath))
            {
                throw new FileNotFoundException($"Before file not found: {beforePath}");
            }

            if (!File.Exists(afterPath))
            {
                throw new FileNotFoundException($"After file not found: {afterPath}");
            }

            var beforeContent = File.ReadAllText(beforePath);
            var afterContent = File.ReadAllText(afterPath);

            var fileName = Path.GetFileName(beforePath);

            return CreatePatch(beforeContent, afterContent, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create patch from files");
            throw;
        }
    }

    public CreatePatchResponse CreatePatchWithOptions(
        string before,
        string after,
        string? fileName = null,
        int contextLines = 3,
        bool ignoreWhitespace = false,
        bool ignoreCase = false)
    {
        try
        {
            var processedBefore = before;
            var processedAfter = after;

            // Apply options
            if (ignoreWhitespace)
            {
                processedBefore = NormalizeWhitespace(processedBefore);
                processedAfter = NormalizeWhitespace(processedAfter);
            }

            if (ignoreCase)
            {
                processedBefore = processedBefore.ToLowerInvariant();
                processedAfter = processedAfter.ToLowerInvariant();
            }

            var response = CreatePatch(processedBefore, processedAfter, fileName);

            // Add options info to summary if applicable
            if (ignoreWhitespace || ignoreCase)
            {
                var options = new List<string>();
                if (ignoreWhitespace) options.Add("ignore-whitespace");
                if (ignoreCase) options.Add("ignore-case");
                response.Summary += $" (options: {string.Join(", ", options)})";
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create patch with options");
            throw;
        }
    }

    private string NormalizeWhitespace(string text)
    {
        // Normalize different types of whitespace
        var lines = text.Split('\n');
        var normalized = new List<string>();

        foreach (var line in lines)
        {
            // Trim trailing whitespace
            var trimmed = line.TrimEnd();

            // Replace multiple spaces with single space
            var collapsed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ");

            normalized.Add(collapsed);
        }

        return string.Join('\n', normalized);
    }

    public bool ApplyPatch(string filePath, string patch)
    {
        try
        {
            _logger.LogInformation("Applying patch to {FilePath}", filePath);

            // This is a simplified patch application
            // In a real implementation, you'd parse the unified diff format properly
            // For now, this is just a placeholder

            _logger.LogWarning("Patch application is not fully implemented yet");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply patch");
            return false;
        }
    }
}