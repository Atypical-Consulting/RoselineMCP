export type ToolKind = 'read' | 'write' | 'diagnostics';

export interface Tool {
  name: string;        // MCP wire name (snake_case)
  title: string;       // human-readable Title advertised to clients (v1.4.0)
  group: string;
  kind: ToolKind;
  summary: string;
  params: string;
  returns: string;     // shape of the `data` payload (nested inside the ToolResult envelope)
  progress?: boolean;  // emits MCP progress notifications
  confirms?: boolean;  // asks the client to confirm via elicitation before writing
}

export const tools: Tool[] = [
  // ── Code navigation (read-only, token-efficient) ──
  {
    name: 'search_symbols', title: 'Search Symbols', group: 'Code navigation', kind: 'read',
    summary: 'Find symbols by wildcard/substring name pattern, or outline a single file.',
    params: 'project, query?, file?, kinds?, max?',
    returns: 'symbols[] (name, fullName, kind, signature, file, line — relative paths; outline mode gives name, kind, signature, line, containingType), totalFound, truncated?',
  },
  {
    name: 'get_symbol_info', title: 'Get Symbol Info', group: 'Code navigation', kind: 'read',
    summary: 'A symbol’s kind, modifiers, signature, base types, interfaces, docs, and definition — the compact go-to-definition.',
    params: 'project, symbol, includeSource?',
    returns: 'name, fullName, kind, signature, then (omitted when empty/absent) modifiers[], baseTypes[], interfaces[], documentation, definitionFile/Line, source',
  },
  {
    name: 'find_references', title: 'Find References', group: 'Code navigation', kind: 'read',
    summary: 'Every reference (use site) of a symbol across the solution, as location + one-line snippet.',
    params: 'project, symbol, includeDefinition?, max?',
    returns: 'references[] (file — relative, line, snippet), totalReferences, truncated?',
  },
  {
    name: 'find_implementations', title: 'Find Implementations', group: 'Code navigation', kind: 'read',
    summary: 'Implementations of an interface/member, overrides, or derived types of a class.',
    params: 'project, symbol, max?',
    returns: 'implementations[] (symbol summaries), totalFound, truncated?',
  },
  {
    name: 'get_call_graph', title: 'Get Call Graph', group: 'Code navigation', kind: 'read',
    summary: 'A depth-bounded caller and/or callee graph for a method, with cycle detection.',
    params: 'project, method, direction?, depth?, max?',
    returns: 'callers?/callees? trees (fullName with simple param-type names, file — relative, line, truncated?, children)',
  },
  {
    name: 'get_type_hierarchy', title: 'Get Type Hierarchy', group: 'Code navigation', kind: 'read',
    summary: 'A type’s base-class chain, implemented interfaces, and/or derived types.',
    params: 'project, type, direction?, max?',
    returns: 'baseTypes?, interfaces?, derivedTypes? (summaries), derivedTypesTruncated?',
  },
  // ── Code editing (write, preview by default, confirms before writing) ──
  {
    name: 'edit_member', title: 'Edit Member', group: 'Code editing', kind: 'write', confirms: true,
    summary: 'Replace, add, or delete a single type member; returns a unified diff. Preview by default.',
    params: 'project, symbol, operation, newSource?, previewOnly?',
    returns: 'operation, target, changedFiles[], patch, previewOnly, applied, notes[]',
  },
  {
    name: 'rename_symbol', title: 'Rename Symbol', group: 'Code editing', kind: 'write', confirms: true, progress: true,
    summary: 'Rename a symbol and update every reference across the solution (Roslyn rename). Preview by default.',
    params: 'project, symbol, newName, previewOnly?',
    returns: 'symbol, newName, changedFiles[], patch, previewOnly, applied, notes[]',
  },
  // ── Diagnostics & fixes ──
  {
    name: 'analyze_solution', title: 'Analyze Solution', group: 'Diagnostics & fixes', kind: 'diagnostics', progress: true,
    summary: 'Analyze an entire C# solution for diagnostics, with filtering. Also accepts an http(s) Git URL (the one open-world tool).',
    params: 'pathOrGit, branch?, include?, exclude?, severity?, maxDiagnostics?',
    returns: 'solution, projects, diagnosticSummary, topDiagnostics[]',
  },
  {
    name: 'list_diagnostics', title: 'List Diagnostics', group: 'Diagnostics & fixes', kind: 'diagnostics',
    summary: 'Detailed diagnostics for a project, with statistics and suggested fixable IDs.',
    params: 'project, ids?, files?, max?',
    returns: 'project, totalDiagnostics, diagnostics[], stats, suggestedFixableIds[]',
  },
  {
    name: 'apply_fixes', title: 'Apply Fixes', group: 'Diagnostics & fixes', kind: 'write', confirms: true, progress: true,
    summary: 'Apply automated code fixes for diagnostic IDs. Preview by default.',
    params: 'project, ids, previewOnly?',
    returns: 'project, fixedCount, fixersApplied[], changedFiles[], patch, notes[], previewOnly',
  },
  {
    name: 'create_patch', title: 'Create Patch', group: 'Diagnostics & fixes', kind: 'diagnostics',
    summary: 'Generate a unified diff between two text versions. Pure text, no filesystem.',
    params: 'before, after, fileName?, ignoreWhitespace?, ignoreCase?',
    returns: 'patch, hasChanges, linesAdded, linesRemoved, fileName, summary',
  },
];

export const groups = ['Code navigation', 'Code editing', 'Diagnostics & fixes'];
