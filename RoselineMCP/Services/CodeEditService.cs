using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoselineMCP.Interfaces;
using RoselineMCP.Models;

namespace RoselineMCP.Services;

/// <summary>
/// Roslyn-backed implementation of <see cref="ICodeEditService"/>. Performs member-level edits and
/// solution-wide renames, returning a unified diff for the change. Follows the same "read-only by
/// default" contract as <c>ApplyFixes</c>: nothing is written to disk unless <c>previewOnly</c> is
/// explicitly set to <see langword="false"/>.
/// </summary>
public class CodeEditService : ICodeEditService
{
    private readonly ILogger<CodeEditService> _logger;
    private readonly IProjectLoader _projectLoader;
    private readonly IDiffService _diffService;
    private readonly IVerificationService _verificationService;

    /// <summary>Initializes a new instance of the <see cref="CodeEditService"/>.</summary>
    public CodeEditService(
        ILogger<CodeEditService> logger,
        IProjectLoader projectLoader,
        IDiffService diffService,
        IVerificationService verificationService)
    {
        _logger = logger;
        _projectLoader = projectLoader;
        _diffService = diffService;
        _verificationService = verificationService;
    }

    /// <summary>
    /// The compile gate, shared by both write paths: verify the in-memory candidate against the
    /// loaded baseline and decide whether the write may go ahead.
    /// </summary>
    /// <returns><see langword="true"/> when the caller should refuse to write.</returns>
    /// <remarks>
    /// Deliberately runs on the candidate <see cref="Solution"/>, which exists only in memory —
    /// Roslyn snapshots are immutable, so a refused edit never had any chance to reach disk.
    /// </remarks>
    private async Task<(VerificationVerdict Verdict, bool Refused)> VerifyAsync(
        Solution baseline,
        Solution candidate,
        bool allowIntroducedErrors,
        int max,
        CancellationToken cancellationToken)
    {
        var verdict = await _verificationService.VerifyAsync(baseline, candidate, max, cancellationToken);
        var introducedCount = verdict.Introduced?.Count ?? 0;
        // The gate is `introduced`, never `compiles`: a repository that was already broken must
        // still be editable, or RoselineMCP is useless on exactly the branches agents are sent to fix.
        return (verdict, introducedCount > 0 && !allowIntroducedErrors);
    }

    /// <summary>The note a refusal carries — it has to say what to do next, not merely that it said no.</summary>
    private static string RefusalNote(VerificationVerdict verdict)
    {
        var count = verdict.Introduced?.Count ?? 0;
        var omitted = verdict.Omitted > 0 ? $" (+{verdict.Omitted} more not shown)" : string.Empty;
        return $"Refused: this change introduces {count} compiler error(s){omitted} — nothing was written. "
            + "Fix the change, or pass allowIntroducedErrors: true to write it anyway.";
    }

    /// <inheritdoc/>
    public async Task<EditMemberResponse> EditMemberAsync(
        string? project,
        string symbol,
        string operation,
        string? newSource,
        bool previewOnly,
        bool allowIntroducedErrors = false,
        int max = 20,
        CancellationToken cancellationToken = default)
    {
        var op = (operation ?? string.Empty).Trim().ToLowerInvariant();
        if (op is not ("replace" or "add" or "delete"))
        {
            throw new ArgumentException($"Invalid operation '{operation}'. Valid values: replace, add, delete.");
        }

        if (op is "replace" or "add" && string.IsNullOrWhiteSpace(newSource))
        {
            throw new ArgumentException($"'newSource' is required for the '{op}' operation.");
        }

        using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);
        var resolved = await SymbolResolver.ResolveOrThrowAsync(loaded.Solution, loaded.Project, symbol, cancellationToken);

        var syntaxRef = resolved.DeclaringSyntaxReferences.FirstOrDefault()
            ?? throw new InvalidOperationException($"'{symbol}' has no source declaration to edit (it is metadata-only).");

        var oldNode = await syntaxRef.GetSyntaxAsync(cancellationToken);
        var document = loaded.Solution.GetDocument(oldNode.SyntaxTree)
            ?? throw new InvalidOperationException($"Could not locate the source document for '{symbol}'.");
        var filePath = document.FilePath
            ?? throw new InvalidOperationException($"The declaration of '{symbol}' has no file path on disk.");

        var originalText = (await document.GetTextAsync(cancellationToken)).ToString();
        var root = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Could not parse the source document for '{symbol}'.");

        var (newRoot, formatEditedNode) = op switch
        {
            "add" => (AddMember(root, oldNode, resolved, symbol, newSource!), true),
            "replace" => (ReplaceMember(root, oldNode, symbol, newSource!), true),
            _ => (DeleteMember(root, oldNode), false)
        };

        // Format only the edited node (tagged with Formatter.Annotation), never the whole file, so
        // unrelated members keep their existing formatting and the diff stays proportional to the change.
        var editedDocument = document.WithSyntaxRoot(newRoot);
        var newDocument = formatEditedNode
            ? await Formatter.FormatAsync(editedDocument, Formatter.Annotation, options: null, cancellationToken)
            : editedDocument;
        var newSourceText = await newDocument.GetTextAsync(cancellationToken);
        var newText = newSourceText.ToString();

        var relativePath = RelativePath(loaded, filePath);
        var response = new EditMemberResponse
        {
            Project = loaded.Project.Name,
            ResolvedPath = loaded.ResolvedPath,
            Operation = op,
            Target = resolved.ToDisplayString(SymbolResolver.FullNameFormat),
            PreviewOnly = previewOnly
        };

        var patch = _diffService.GenerateUnifiedDiff(originalText, newText, $"a/{relativePath}", $"b/{relativePath}");
        if (string.IsNullOrWhiteSpace(patch))
        {
            response.Notes.Add("No changes were produced by the edit.");
            return response;
        }

        response.Patch = patch;
        response.ChangedFiles.Add(relativePath);

        // The candidate solution: the edit applied in memory, nothing on disk. This is what the
        // compiler is asked about, before the write and before any human is asked to approve one.
        var candidate = loaded.Solution.WithDocumentText(document.Id, newSourceText);
        var (verdict, refused) = await VerifyAsync(
            loaded.Solution, candidate, allowIntroducedErrors, max, cancellationToken);
        response.Verification = verdict;

        if (refused)
        {
            _logger.LogInformation(
                "Refused edit of '{Symbol}': it introduces {Count} compiler error(s)",
                symbol, verdict.Introduced?.Count ?? 0);
            response.Notes.Add(RefusalNote(verdict));
            return response;
        }

        if (!previewOnly)
        {
            // Write with the file's original encoding (BOM included) — see SourceTextWriter.
            await SourceTextWriter.WriteAsync(filePath, newSourceText, cancellationToken);
            response.Applied = true;
            response.Notes.Add($"Wrote changes to {relativePath}.");
        }
        else
        {
            response.Notes.Add("Preview mode - no changes were saved to disk.");
        }

        return response;
    }

    private static SyntaxNode AddMember(SyntaxNode root, SyntaxNode oldNode, ISymbol resolved, string symbol, string newSource)
    {
        if (resolved is not INamedTypeSymbol)
        {
            throw new ArgumentException($"The 'add' operation targets a container type, but '{symbol}' is a {SymbolResolver.KindOf(resolved)}.");
        }

        if (oldNode is not TypeDeclarationSyntax typeDeclaration)
        {
            throw new ArgumentException($"'{symbol}' is not a class, struct, interface, or record that can contain members.");
        }

        // Elastic leading trivia + the format annotation let the scoped formatter place the new
        // member on its own line, correctly indented, without reformatting the rest of the file.
        var member = ParseMember(newSource)
            .WithLeadingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed)
            .WithAdditionalAnnotations(Formatter.Annotation);
        return root.ReplaceNode(typeDeclaration, typeDeclaration.AddMembers(member));
    }

    private static SyntaxNode ReplaceMember(SyntaxNode root, SyntaxNode oldNode, string symbol, string newSource)
    {
        // For a field/event symbol, oldNode is the VariableDeclaratorSyntax; the replaceable unit is
        // the enclosing field/event declaration.
        var memberNode = EnclosingMember(oldNode)
            ?? throw new ArgumentException($"'{symbol}' is not a replaceable member declaration.");

        if (oldNode is VariableDeclaratorSyntax
            && memberNode is BaseFieldDeclarationSyntax field
            && field.Declaration.Variables.Count > 1)
        {
            throw new ArgumentException(
                $"'{symbol}' is one of several variables declared together; replace is not supported for multi-variable field/event declarations.");
        }

        var parsed = ParseMember(newSource);
        var member = parsed
            .WithLeadingTrivia(PreferLeadingTrivia(parsed, memberNode))
            .WithTrailingTrivia(memberNode.GetTrailingTrivia())
            .WithAdditionalAnnotations(Formatter.Annotation);
        return root.ReplaceNode(memberNode, member);
    }

    private static SyntaxNode DeleteMember(SyntaxNode root, SyntaxNode oldNode)
    {
        // A field/event symbol's declaring node is a VariableDeclaratorSyntax. Remove the whole
        // field/event declaration when it declares only this variable, otherwise remove just this
        // declarator so the sibling variables' declaration stays valid.
        if (oldNode is VariableDeclaratorSyntax declarator
            && declarator.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>() is { } field)
        {
            var nodeToRemove = field.Declaration.Variables.Count > 1 ? (SyntaxNode)declarator : field;
            return root.RemoveNode(nodeToRemove, SyntaxRemoveOptions.KeepNoTrivia)
                ?? throw new InvalidOperationException("Deleting the member would leave an empty or invalid tree.");
        }

        return root.RemoveNode(oldNode, SyntaxRemoveOptions.KeepNoTrivia)
            ?? throw new InvalidOperationException("Deleting the member would leave an empty or invalid tree.");
    }

    /// <summary>The member declaration to edit for a symbol — the node itself, or the enclosing field/event declaration for a variable declarator.</summary>
    private static MemberDeclarationSyntax? EnclosingMember(SyntaxNode node) =>
        node as MemberDeclarationSyntax ?? node.FirstAncestorOrSelf<MemberDeclarationSyntax>();

    /// <summary>
    /// Keeps the caller's own leading comments/doc-comments when <paramref name="parsed"/> supplies
    /// any (so a documentation update in newSource is not lost), otherwise preserves the existing
    /// member's leading trivia (its current doc comment and indentation).
    /// </summary>
    private static SyntaxTriviaList PreferLeadingTrivia(MemberDeclarationSyntax parsed, SyntaxNode existingMember)
    {
        var parsedLeading = parsed.GetLeadingTrivia();
        var parsedHasComment = parsedLeading.Any(t =>
            t.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

        return parsedHasComment ? parsedLeading : existingMember.GetLeadingTrivia();
    }

    private static MemberDeclarationSyntax ParseMember(string newSource)
    {
        var member = SyntaxFactory.ParseMemberDeclaration(newSource);
        if (member == null)
        {
            throw new ArgumentException("'newSource' could not be parsed as a C# member declaration.");
        }

        var error = member.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (error != null)
        {
            throw new ArgumentException($"'newSource' has a syntax error: {error.GetMessage()}");
        }

        return member;
    }

    /// <inheritdoc/>
    public async Task<RenameSymbolResponse> RenameSymbolAsync(
        string? project,
        string symbol,
        string newName,
        bool previewOnly,
        bool allowIntroducedErrors = false,
        int max = 20,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new ArgumentException("'newName' is required.");
        }

        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            throw new ArgumentException($"'{newName}' is not a valid C# identifier.");
        }

        // Progress values must strictly increase (MCP requirement), so the three phases are 1/2/3.
        progress?.Report(new ProgressNotificationValue { Progress = 1, Message = "Loading project via MSBuild…" });
        using var loaded = await _projectLoader.LoadAsync(project, cancellationToken);

        progress?.Report(new ProgressNotificationValue { Progress = 2, Message = $"Resolving symbol '{symbol}'…" });
        var resolved = await SymbolResolver.ResolveOrThrowAsync(loaded.Solution, loaded.Project, symbol, cancellationToken);

        if (!resolved.Locations.Any(l => l.IsInSource))
        {
            throw new InvalidOperationException($"'{symbol}' is metadata-only and cannot be renamed.");
        }

        var originalSolution = loaded.Solution;
        progress?.Report(new ProgressNotificationValue { Progress = 3, Message = "Renaming across the solution…" });
        var newSolution = await Renamer.RenameSymbolAsync(
            originalSolution, resolved, new SymbolRenameOptions(), newName, cancellationToken);

        var response = new RenameSymbolResponse
        {
            Project = loaded.Project.Name,
            ResolvedPath = loaded.ResolvedPath,
            Symbol = resolved.ToDisplayString(SymbolResolver.FullNameFormat),
            NewName = newName,
            PreviewOnly = previewOnly
        };

        var patchBuilder = new StringBuilder();
        var filesToWrite = new List<(string Path, SourceText Text)>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectChange in newSolution.GetChanges(originalSolution).GetProjectChanges())
        {
            foreach (var documentId in projectChange.GetChangedDocuments())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var oldDocument = originalSolution.GetDocument(documentId);
                var newDocument = newSolution.GetDocument(documentId);
                if (oldDocument?.FilePath == null || newDocument == null || !seenPaths.Add(oldDocument.FilePath))
                {
                    continue;
                }

                var oldText = (await oldDocument.GetTextAsync(cancellationToken)).ToString();
                var newSourceText = await newDocument.GetTextAsync(cancellationToken);
                var newText = newSourceText.ToString();
                if (oldText == newText)
                {
                    continue;
                }

                var relativePath = RelativePath(loaded, oldDocument.FilePath);
                var diff = _diffService.GenerateUnifiedDiff(oldText, newText, $"a/{relativePath}", $"b/{relativePath}");
                if (string.IsNullOrWhiteSpace(diff))
                {
                    continue;
                }

                patchBuilder.AppendLine(diff);
                response.ChangedFiles.Add(relativePath);
                filesToWrite.Add((oldDocument.FilePath, newSourceText));
            }
        }

        if (filesToWrite.Count == 0)
        {
            response.Notes.Add("Rename produced no changes.");
            return response;
        }

        response.Patch = patchBuilder.ToString();

        if (!previewOnly)
        {
            foreach (var (path, text) in filesToWrite)
            {
                // Write with each file's original encoding (BOM included) — see SourceTextWriter.
                await SourceTextWriter.WriteAsync(path, text, cancellationToken);
            }

            response.Applied = true;
            response.Notes.Add($"Applied rename to {filesToWrite.Count} file(s).");
        }
        else
        {
            response.Notes.Add("Preview mode - no changes were saved to disk.");
        }

        return response;
    }

    /// <summary>
    /// Emitted paths are solution-root-relative with forward slashes (falling back to the project
    /// directory when no <c>.sln</c> was loaded) — the same rule the navigation tools and
    /// <c>ApplyFixes</c> use, so a given file has one canonical path across every tool's output.
    /// </summary>
    private static string RelativePath(LoadedProject loaded, string filePath)
    {
        var baseDirectory = Path.GetDirectoryName(loaded.Solution.FilePath ?? loaded.Project.FilePath);
        return SymbolResolver.Relativize(filePath, baseDirectory) ?? filePath;
    }
}
