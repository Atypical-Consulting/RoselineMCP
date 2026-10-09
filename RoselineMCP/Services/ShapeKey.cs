using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoselineMCP.Services;

/// <summary>
/// The retrieval index of <c>suggest_fix_examples</c>: a deterministic string built from a node's
/// <see cref="SyntaxKind"/>, its two nearest ancestors' kinds and — when resolvable — the
/// declaration key of the symbol referenced there. Two sites match when their keys are equal.
/// </summary>
public static class ShapeKey
{
    /// <summary>Kinds-only components, for a cheap prefilter before any semantic work.</summary>
    public static string KindsOf(SyntaxNode node)
        => $"{node.Kind()}|{node.Parent?.Kind().ToString() ?? "-"}|{node.Parent?.Parent?.Kind().ToString() ?? "-"}";

    /// <summary>Builds the key and reports which components it carries. Never throws.</summary>
    public static (string Key, string MatchedOn) From(SyntaxNode node, SemanticModel? model)
    {
        var symbolKey = "-";
        if (model is not null)
        {
            try
            {
                // The symbol *referenced* at the site. A declared symbol is deliberately not used:
                // every declaration is unique, so it would make no two sites ever match.
                var symbol = model.GetSymbolInfo(node).Symbol;
                if (symbol is not null)
                {
                    symbolKey = SymbolResolver.DeclarationKeyOf(symbol.OriginalDefinition);
                }
            }
            catch (Exception)
            {
                // A node the model cannot resolve degrades to a kinds-only key rather than failing.
            }
        }

        return ($"{KindsOf(node)}|{symbolKey}", symbolKey == "-" ? "kinds" : "kinds+symbol");
    }
}
