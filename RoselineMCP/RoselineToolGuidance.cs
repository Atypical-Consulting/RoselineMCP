namespace RoselineMCP;

/// <summary>
/// Server-level MCP <c>instructions</c> sent to the client on initialize. This is the one message
/// the model reliably sees at tool-selection time, so it states a concrete decision policy: prefer
/// RoselineMCP's structural tools over reading whole files. Without it the model defaults to
/// <c>Read</c>/<c>Grep</c> and never exercises the token savings these tools exist to provide.
/// </summary>
internal static class RoselineToolGuidance
{
    public const string Instructions =
        """
        RoselineMCP gives you Roslyn-powered, token-efficient navigation of a C# solution. When a task
        involves understanding EXISTING C# code, prefer these tools over reading whole files or grepping —
        they return only the structure you need and cost far fewer tokens, especially on large files:

        - Locate a type/method/property, or see a file's shape → `search_symbols` (instead of opening the file).
        - Understand one symbol — signature, docs, base types/interfaces, definition → `get_symbol_info`
          (use `includeSource: true` to read its body instead of Read).
        - Find where a symbol is used → `find_references`. Who implements/overrides it → `find_implementations`.
          Who calls a method (and what it calls) → `get_call_graph`. A type's base/derived tree → `get_type_hierarchy`.
        - Change code → `edit_member` / `rename_symbol` (surgical diffs) instead of rewriting whole files.

        Reserve `Read`/`Grep` for non-C# files, or when you need the exact full text of a specific member
        (`get_symbol_info` with `includeSource: true` covers that too). Every tool takes an optional `project`
        (a project name, directory, or `.csproj`/`.sln` path); omit it and the solution/project is
        auto-discovered from the working directory.
        """;
}
