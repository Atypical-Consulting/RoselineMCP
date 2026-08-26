export type ToolKind = 'read' | 'write' | 'diagnostics';

export interface Tool {
  name: string;        // MCP wire name (snake_case)
  title: string;       // human-readable Title advertised to clients (v1.4.0)
  group: ToolGroup;    // one of `groups` — typed, so a typo is a compile error, not a missing card
  kind: ToolKind;
  summary: string;
  params: string;
  returns: string;     // shape of the `data` payload (nested inside the ToolResult envelope)
  progress?: boolean;  // emits MCP progress notifications
  verifies?: boolean;  // compiles the candidate change in memory and refuses writes that introduce compiler errors
  confirms?: boolean;  // asks the client to confirm via elicitation before writing (unless RoselineMCP:ConfirmDestructiveWrites is false); an unanswered prompt expires after RoselineMCP:ConfirmDestructiveWritesTimeout (5 min) into a preview, never a write
  analyzerLoad?: boolean; // returns an analyzerLoad block naming any analyzer reference that contributed nothing (CLAUDE.md § MCP Tools Available calls these "the diagnostics tools (1–3)")
}

export const tools: Tool[] = [
  // ── Code navigation (read-only, token-efficient) ──
  {
    name: 'search_symbols', title: 'Search Symbols', group: 'Code navigation', kind: 'read',
    summary: 'Find symbols by wildcard/substring name pattern, or outline a single file.',
    params: 'project?, query?, file?, kinds?, max?',
    returns: 'resolvedPath, symbols[] (name, fullName, kind, signature, file, line — relative paths; outline mode gives name, kind, signature, line, containingType), totalFound, truncated?',
  },
  {
    name: 'get_symbol_info', title: 'Get Symbol Info', group: 'Code navigation', kind: 'read',
    summary: 'A symbol’s kind, modifiers, signature, base types, interfaces, docs, and definition — the compact go-to-definition.',
    params: 'project?, symbol, includeSource?',
    returns: 'resolvedPath, name, fullName, kind, signature, then (omitted when empty/absent) modifiers[], baseTypes[], interfaces[], documentation, definitionFile/Line, source',
  },
  {
    name: 'find_references', title: 'Find References', group: 'Code navigation', kind: 'read',
    summary: 'Every reference (use site) of a symbol across the solution, as location + one-line snippet.',
    params: 'project?, symbol, includeDefinition?, max?',
    returns: 'resolvedPath, references[] (file — relative, line, snippet), totalReferences, truncated?',
  },
  {
    name: 'find_implementations', title: 'Find Implementations', group: 'Code navigation', kind: 'read',
    summary: 'Implementations of an interface/member, overrides, or derived types of a class.',
    params: 'project?, symbol, max?',
    returns: 'resolvedPath, implementations[] (symbol summaries), totalFound, truncated?',
  },
  {
    name: 'get_call_graph', title: 'Get Call Graph', group: 'Code navigation', kind: 'read',
    summary: 'A depth-bounded caller and/or callee graph for a method, with cycle detection.',
    params: 'project?, method, direction?, depth?, max?',
    returns: 'resolvedPath, callers?/callees? trees (fullName with simple param-type names, file — relative, line, truncated?, children)',
  },
  {
    name: 'get_type_hierarchy', title: 'Get Type Hierarchy', group: 'Code navigation', kind: 'read',
    summary: 'A type’s base-class chain, implemented interfaces, and/or derived types.',
    params: 'project?, type, direction?, max?',
    returns: 'resolvedPath, baseTypes?, interfaces?, derivedTypes? (summaries), derivedTypesTruncated?',
  },
  {
    name: 'get_symbol_at_position', title: 'Get Symbol At Position', group: 'Code navigation', kind: 'read',
    summary: 'The symbol at a file:line(:column) — turn a diagnostic, stack trace, or grep hit into a symbol name.',
    params: 'project?, file, line, column?',
    returns: 'resolvedPath, name, fullName, kind, signature, isDeclaration, then (omitted when empty/absent) containingType, documentation, definitionFile/Line',
  },
  // ── Code editing (write, preview by default, confirms before writing) ──
  {
    name: 'edit_member', title: 'Edit Member', group: 'Code editing', kind: 'write', confirms: true, verifies: true,
    summary: 'Replace, add, or delete a single type member; returns a unified diff. Preview by default, and refused if it would not compile.',
    params: 'project?, symbol, operation, newSource?, previewOnly?, allowIntroducedErrors?, max?',
    returns: 'project, resolvedPath, operation, target, changedFiles[], patch, previewOnly, applied, verification?, notes[]',
  },
  {
    name: 'rename_symbol', title: 'Rename Symbol', group: 'Code editing', kind: 'write', confirms: true, progress: true, verifies: true,
    summary: 'Rename a symbol and update every reference across the solution (Roslyn rename). Preview by default, and refused if it would break a downstream project.',
    params: 'project?, symbol, newName, previewOnly?, allowIntroducedErrors?, max?',
    returns: 'project, resolvedPath, symbol, newName, changedFiles[], patch, previewOnly, applied, verification?, notes[]',
  },
  // ── Diagnostics & fixes ──
  {
    name: 'analyze_solution', title: 'Analyze Solution', group: 'Diagnostics & fixes', kind: 'diagnostics', progress: true, analyzerLoad: true,
    summary: 'Analyze an entire C# solution for diagnostics, with filtering. Also accepts an http(s) Git URL (the one open-world tool).',
    params: 'pathOrGit, branch?, include?, exclude?, severity?, maxDiagnostics?',
    returns: 'solution, projects, diagnosticSummary, topDiagnostics[], analyzerLoad? (every analyzer reference that contributed nothing, and why — merged across projects; omitted when all contributed)',
  },
  {
    name: 'list_diagnostics', title: 'List Diagnostics', group: 'Diagnostics & fixes', kind: 'diagnostics', analyzerLoad: true,
    summary: 'Detailed diagnostics for a project, with statistics and suggested fixable IDs — fixers from Roslyn, the bundled catalog and the project’s own analyzer references.',
    params: 'project?, ids?, files?, max?',
    returns: 'project, resolvedPath, totalDiagnostics, diagnostics[], stats, suggestedFixableIds[], analyzerLoad? (analyzersRan, referencesConsulted, referencesContributing, analyzersLoaded, notes[] — omitted when every reference contributed)',
  },
  {
    name: 'apply_fixes', title: 'Apply Fixes', group: 'Diagnostics & fixes', kind: 'write', confirms: true, progress: true, verifies: true, analyzerLoad: true,
    summary: 'Apply automated code fixes for diagnostic IDs to one project — a .sln target fixes its primary project and names the ones it skipped. Preview by default, and refused if the fixes would not compile.',
    params: 'ids, project?, previewOnly?, allowIntroducedErrors?, max?',
    returns: 'project, resolvedPath, fixedCount, fixersApplied[], changedFiles[] (relative to the resolvedPath directory), patch, notes[] (scope: fixed/skipped projects, linked files; per-ID status), previewOnly, applied, verification?, analyzerLoad? (omitted when every analyzer reference contributed)',
  },
  {
    name: 'check_compilation', title: 'Check Compilation', group: 'Diagnostics & fixes', kind: 'diagnostics',
    summary: 'Does this compile right now, and what broke? Compiler errors only, in under a second on a warm workspace — the replacement for `dotnet build` in an edit loop.',
    params: 'project?, max?',
    returns: 'resolvedPath, compiles, errors[], omitted?, scope[], scopeComplete, notes[]',
  },
  {
    name: 'create_patch', title: 'Create Patch', group: 'Diagnostics & fixes', kind: 'diagnostics',
    summary: 'Generate a unified diff between two text versions. Pure text, no filesystem.',
    params: 'before, after, fileName?, ignoreWhitespace?, ignoreCase?',
    returns: 'patch, hasChanges, linesAdded, linesRemoved, fileName, summary',
  },
];

// `as const` so `ToolGroup` is the union of these three literals rather than `string`. Both pages
// render their grids with `tools.filter((t) => t.group === g)` over this list, so an entry whose
// group is not in it would render no card at all while still counting towards `toolCount` — "N tools"
// above N-1 cards, i.e. #197 again. Typing `Tool.group` makes that a compile error.
export const groups = ['Code navigation', 'Code editing', 'Diagnostics & fixes'] as const;

export type ToolGroup = (typeof groups)[number];

// ── The tool count, derived ──
// The headings on tools.astro and index.astro used to restate this number in prose, and drifted
// (they still said "Thirteen" after check_compilation landed as tool 14). Both now read it from the
// array above, so the count cannot go stale when a tool is added.

/** How many tools the array holds. */
export const toolCount = tools.length;

// Capitalised, because every heading that uses one opens a sentence with it. Module-private: a
// public Capitalised word invites a mid-sentence use that reads wrong.
const numberWords = [
  'Zero', 'One', 'Two', 'Three', 'Four', 'Five', 'Six', 'Seven', 'Eight', 'Nine', 'Ten',
  'Eleven', 'Twelve', 'Thirteen', 'Fourteen', 'Fifteen', 'Sixteen', 'Seventeen', 'Eighteen',
  'Nineteen', 'Twenty',
];

// A count as an English word, falling back to digits outside the mapped range rather than rendering
// nothing (the spec's validation rule). `lower` reuses the same 0–20 table rather than a second one,
// so the two spellings can never disagree — a mid-sentence phrase ("seven navigation tools") needs
// the lowercase form, a heading ("Fourteen tools") the capitalised one.
const numberWord = (n: number, opts: { lower?: boolean } = {}): string => {
  const word = numberWords[n] ?? String(n);
  return opts.lower ? word.toLowerCase() : word;
};

const plural = (n: number) => (n === 1 ? 'tool' : 'tools');

/** The noun phrase both headings open with: "Fourteen tools" — and "One tool" if it ever is. */
export const toolCountPhrase = `${numberWord(toolCount)} ${plural(toolCount)}`;

// The home page's blurb counts only the code-intelligence surface — everything that is not the
// original diagnostics group — so it needs its own derived count rather than `toolCount`. Naming the
// group through `ToolGroup` rather than a second literal list keeps a rename a compile error; a
// stale copy of the group names here would silently count zero.
const diagnosticsGroup: ToolGroup = 'Diagnostics & fixes';
const codeIntelligenceToolCount = tools.filter((t) => t.group !== diagnosticsGroup).length;

/** "Nine code-intelligence tools" — and the singular if it ever comes to that. */
export const codeIntelligenceToolPhrase =
  `${numberWord(codeIntelligenceToolCount)} code-intelligence ${plural(codeIntelligenceToolCount)}`;

// ── Per-kind counts, derived ──
// tools.astro used to hand-restate these two mid-sentence ("The seven navigation tools…", "The three
// write-capable tools…") — correct only until a tool moves in or out of the kind, exactly #197's
// shape. `kind: 'read'` is entirely the Code navigation group; `kind: 'write'` is Code editing plus
// apply_fixes. Lowercase because both sentences use the count mid-clause, not at the sentence start.
const readToolCount = tools.filter((t) => t.kind === 'read').length;
const writeToolCount = tools.filter((t) => t.kind === 'write').length;

/** "seven navigation tools" — mid-sentence, lowercase. */
export const readToolPhrase = `${numberWord(readToolCount, { lower: true })} navigation ${plural(readToolCount)}`;

/** "three write-capable tools" — mid-sentence, lowercase. */
export const writeToolPhrase = `${numberWord(writeToolCount, { lower: true })} write-capable ${plural(writeToolCount)}`;

// ── The analyzerLoad-reporting tools, derived ──
// tools.astro used to name these three inline and call them "the three diagnostics tools" — but that
// collides with `kind: 'diagnostics'`, which is four tools (analyze_solution, list_diagnostics,
// check_compilation, create_patch), and apply_fixes is `kind: 'write'`. The sentence was never really
// about `kind`; it was about which tools return an `analyzerLoad` block (CLAUDE.md § MCP Tools
// Available: "the diagnostics tools (1–3)"). Deriving both the count and the member list from the
// `analyzerLoad` flag keeps the sentence from drifting in either direction again.
export const analyzerLoadTools = tools.filter((t) => t.analyzerLoad);

/** "three tools" — the count of `analyzerLoad`-reporting tools, mid-sentence, lowercase. */
export const analyzerLoadToolPhrase =
  `${numberWord(analyzerLoadTools.length, { lower: true })} ${plural(analyzerLoadTools.length)}`;
