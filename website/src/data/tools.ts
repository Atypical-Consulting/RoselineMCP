export type ToolKind = 'read' | 'write' | 'diagnostics';

export interface Tool {
  name: string;        // MCP wire name (snake_case)
  group: string;
  kind: ToolKind;
  summary: string;
  params: string;
  returns: string;
  isNew?: boolean;
}

export const tools: Tool[] = [
  // ── New: code navigation (read-only, token-efficient) ──
  {
    name: 'search_symbols', group: 'Code navigation', kind: 'read', isNew: true,
    summary: 'Find symbols by wildcard/substring name pattern, or outline a single file.',
    params: 'project, query?, file?, kinds?, max?',
    returns: 'symbols[] (name, fullName, kind, signature, accessibility, file, line), totalFound, truncated',
  },
  {
    name: 'get_symbol_info', group: 'Code navigation', kind: 'read', isNew: true,
    summary: 'A symbol’s kind, modifiers, signature, base types, interfaces, docs, and definition — the compact go-to-definition.',
    params: 'project, symbol, includeSource?',
    returns: 'name, fullName, kind, accessibility, modifiers[], signature, baseTypes[], interfaces[], documentation, definitionFile/Line, source?',
  },
  {
    name: 'find_references', group: 'Code navigation', kind: 'read', isNew: true,
    summary: 'Every reference (use site) of a symbol across the solution, as location + one-line snippet.',
    params: 'project, symbol, includeDefinition?, max?',
    returns: 'references[] (file, line, column, snippet), totalReferences, truncated',
  },
  {
    name: 'find_implementations', group: 'Code navigation', kind: 'read', isNew: true,
    summary: 'Implementations of an interface/member, overrides, or derived types of a class.',
    params: 'project, symbol, max?',
    returns: 'implementations[] (symbol summaries), totalFound, truncated',
  },
  {
    name: 'get_call_graph', group: 'Code navigation', kind: 'read', isNew: true,
    summary: 'A depth-bounded caller and/or callee graph for a method, with cycle detection.',
    params: 'project, method, direction?, depth?, max?',
    returns: 'callers?/callees? trees (fullName, signature, file, line, truncated, children)',
  },
  {
    name: 'get_type_hierarchy', group: 'Code navigation', kind: 'read', isNew: true,
    summary: 'A type’s base-class chain, implemented interfaces, and/or derived types.',
    params: 'project, type, direction?, max?',
    returns: 'baseTypes?, interfaces?, derivedTypes? (summaries), derivedTypesTruncated',
  },
  // ── New: code editing (write, preview by default) ──
  {
    name: 'edit_member', group: 'Code editing', kind: 'write', isNew: true,
    summary: 'Replace, add, or delete a single type member; returns a unified diff. Preview by default.',
    params: 'project, symbol, operation, newSource?, previewOnly?',
    returns: 'operation, target, changedFiles[], patch, previewOnly, applied, notes[]',
  },
  {
    name: 'rename_symbol', group: 'Code editing', kind: 'write', isNew: true,
    summary: 'Rename a symbol and update every reference across the solution (Roslyn rename). Preview by default.',
    params: 'project, symbol, newName, previewOnly?',
    returns: 'symbol, newName, changedFiles[], patch, previewOnly, applied, notes[]',
  },
  // ── Existing: diagnostics & fixes ──
  {
    name: 'analyze_solution', group: 'Diagnostics & fixes', kind: 'diagnostics',
    summary: 'Analyze an entire C# solution for diagnostics, with filtering. Also accepts an http(s) Git URL.',
    params: 'pathOrGit, branch?, include?, exclude?, severity?, maxDiagnostics?',
    returns: 'solution, projects, diagnosticSummary, topDiagnostics[]',
  },
  {
    name: 'list_diagnostics', group: 'Diagnostics & fixes', kind: 'diagnostics',
    summary: 'Detailed diagnostics for a project, with statistics and suggested fixable IDs.',
    params: 'project, ids?, files?, max?',
    returns: 'project, totalDiagnostics, diagnostics[], stats, suggestedFixableIds[]',
  },
  {
    name: 'apply_fixes', group: 'Diagnostics & fixes', kind: 'write',
    summary: 'Apply automated code fixes for diagnostic IDs. Preview by default.',
    params: 'project, ids, previewOnly?',
    returns: 'project, fixedCount, fixersApplied[], changedFiles[], patch, notes[], previewOnly',
  },
  {
    name: 'create_patch', group: 'Diagnostics & fixes', kind: 'diagnostics',
    summary: 'Generate a unified diff between two text versions. Pure text, no filesystem.',
    params: 'before, after, fileName?, ignoreWhitespace?, ignoreCase?',
    returns: 'patch, hasChanges, linesAdded, linesRemoved, fileName, summary',
  },
];

export const groups = ['Code navigation', 'Code editing', 'Diagnostics & fixes'];
