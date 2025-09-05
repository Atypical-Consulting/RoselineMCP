using Microsoft.Extensions.Logging;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Service for generating unified diff patches between text versions.
/// </summary>
public class PatchService : IPatchService
{
    private readonly ILogger<PatchService> _logger;
    private readonly IDiffService _diffService;

    /// <summary>
    /// Initializes a new instance of the PatchService.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="diffService">Service for generating diffs.</param>
    public PatchService(ILogger<PatchService> logger, IDiffService diffService)
    {
        _logger = logger;
        _diffService = diffService;
    }

    /// <inheritdoc/>
    public CreatePatchResponse CreatePatch(string before, string after, string? fileName = null)
    {
        try
        {
            var response = new CreatePatchResponse
            {
                FileName = fileName ?? "file.txt"
            };

            // Generate the diff
            var patch = _diffService.GenerateUnifiedDiff(
                before,
                after,
                $"a/{response.FileName}",
                $"b/{response.FileName}");

            response.HasChanges = !string.IsNullOrEmpty(patch);

            if (!response.HasChanges)
            {
                response.Summary = "No changes detected";
                return response;
            }

            // Calculate statistics from the patch
            var linesAdded = 0;
            var linesRemoved = 0;

            var patchLines = patch.Split('\n');
            foreach (var line in patchLines)
            {
                if (line.StartsWith("+") && !line.StartsWith("+++"))
                {
                    linesAdded++;
                }
                else if (line.StartsWith("-") && !line.StartsWith("---"))
                {
                    linesRemoved++;
                }
            }

            response.LinesAdded = linesAdded;
            response.LinesRemoved = linesRemoved;

            // Set the patch
            response.Patch = patch;

            // Create summary
            var summaryParts = new List<string>();
            if (linesAdded > 0) summaryParts.Add($"+{linesAdded}");
            if (linesRemoved > 0) summaryParts.Add($"-{linesRemoved}");

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


    /// <inheritdoc/>
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

    /// <inheritdoc/>
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
                processedBefore = _diffService.NormalizeWhitespace(processedBefore);
                processedAfter = _diffService.NormalizeWhitespace(processedAfter);
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


    /// <inheritdoc/>
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