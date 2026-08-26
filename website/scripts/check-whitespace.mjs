#!/usr/bin/env node
// Guards against #217: Astro's whitespace collapse strips the space at a text-to-tag/expression
// line-wrap boundary (a newline whose only content is indentation, sitting directly between the
// end of a text run and the start of an adjacent tag-open/tag-close or `{expression}`, is dropped
// entirely instead of collapsing to a single space — invisible in the `.astro` source, only
// visible in the built HTML). Confirmed empirically against `website/dist/**/*.html` for four
// shapes, in both directions:
//   1. a word character immediately followed by an inline tag-open: <code, <em, <strong, <a
//   2. a word character immediately followed by `(<code` (a parenthetical opening on a `<code>`)
//   3. an inline tag-close (</code>, </em>, </strong>, </a>) immediately followed by a word
//      character or `(` — the *other* direction; three of #217's own instances slipped the first
//      audit pass because it only checked direction 1/2, and only surfaced once the fix for
//      direction 1/2 was rebuilt and re-grepped (see the PR's Findings for the list)
//   4. a `}` (closing an `{expression}`) immediately followed by a word character
//
// This is a text-pattern match, not a semantic one (see the issue's Validation rule): it is scoped
// to these specific shapes so it doesn't fire on deliberate tight markup (e.g. `<code>foo</code>bar`
// where `bar` is a genuine suffix) or on non-prose adjacency (script/style content, JSX code).
//
// Run after `npm run build`; exits non-zero and prints every offending file/snippet, or exits 0
// silently. Not wired into `npm run build`/`package.json` yet — see the PR's Follow-ups.
//
// Usage: node scripts/check-whitespace.mjs [distDir]

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const distDir = process.argv[2] ?? join(import.meta.dirname, '..', 'dist');

function listHtmlFiles(dir) {
  const out = [];
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) out.push(...listHtmlFiles(full));
    else if (entry.endsWith('.html')) out.push(full);
  }
  return out;
}

// Excludes <script>/<style> bodies, which routinely contain minified JS/CSS whose own syntax
// legitimately glues a `}` or `>` against a following word/letter (template literals, selectors) —
// none of that is prose, so it's not this bug.
function stripScriptsAndStyles(html) {
  return html.replace(/<script[\s\S]*?<\/script>/gi, ' ').replace(/<style[\s\S]*?<\/style>/gi, ' ');
}

const PATTERNS = [
  { name: 'word before inline tag-open', re: /[A-Za-z0-9](\(<code|<code[ >]|<em[ >]|<strong[ >]|<a[ >])/g },
  { name: 'inline tag-close before word or (', re: /(<\/code>|<\/em>|<\/strong>|<\/a>)[A-Za-z0-9(]/g },
  { name: 'expression-close before word', re: /\}[A-Za-z0-9]/g },
];

let failures = [];

let htmlFiles;
try {
  htmlFiles = listHtmlFiles(distDir);
} catch (err) {
  console.error(`check-whitespace: could not read ${distDir} — did you run "npm run build" first?`);
  console.error(String(err));
  process.exit(1);
}

for (const file of htmlFiles) {
  const raw = readFileSync(file, 'utf8');
  const text = stripScriptsAndStyles(raw);
  const rel = relative(process.cwd(), file);
  for (const { name, re } of PATTERNS) {
    for (const m of text.matchAll(re)) {
      const start = Math.max(0, m.index - 40);
      const end = Math.min(text.length, m.index + 40);
      failures.push(`${rel}: [${name}] …${text.slice(start, end)}…`);
    }
  }
}

if (failures.length > 0) {
  console.error(`check-whitespace: ${failures.length} missing-space instance(s) found:\n`);
  for (const f of failures) console.error(`  ${f}`);
  console.error('\nA line-wrap in the .astro source put a bare word immediately next to a tag');
  console.error('open/close or {expression} boundary — see issue #217. Keep the boundary word and');
  console.error('its neighboring tag/expression on the same source line.');
  process.exit(1);
}

process.exit(0);
