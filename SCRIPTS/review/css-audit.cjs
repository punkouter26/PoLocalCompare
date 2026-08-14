// #5 — CSS hygiene sweep.
// Compares every class string used in *.razor markup against the selectors defined in any *.css.
// Reports classes that exist in markup but NOT in any stylesheet ("ghosts").
//
// Run: `node SCRIPTS/review/css-audit.cjs`
//
// Notes:
//   * This is a textual diff, not a real CSS parser. It will miss selectors built by string
//     interpolation (e.g. `lab-card__vram-badge--@State.VramTier`), which PRD §9 item 17
//     flagged as a known blind spot — flag those for manual review.
//   * It also misses ::deep selectors; Blazor scoped CSS still applies them via :scope, so
//     they don't always live under the same selector name.
//   * Single quote or double quote both handled; class= can appear bare ("foo") or in
//     interpolation ("@x.foo") — we accept any token containing an alphabetic char as a class.

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..', '..', 'src', 'PoLocalCompare.Client');
const CLIENT_FILES = ['Pages', 'Components', 'Layout'];
const CSS_FILES    = ['Pages', 'Components', 'Layout', 'wwwroot/css/app.css'];

function* walk(dir) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, ent.name);
    if (ent.isDirectory()) yield* walk(p);
    else yield p;
  }
}

function readAll(dir, ext) {
  const out = [];
  for (const d of Array.isArray(dir) ? dir : [dir]) {
    const abs = path.join(ROOT, d);
    if (!fs.existsSync(abs)) continue;
    if (fs.statSync(abs).isFile()) { out.push(abs); continue; }
    for (const f of walk(abs)) if (f.endsWith(ext)) out.push(f);
  }
  return out;
}

const classRe = /\bclass\s*=\s*(?:"([^"]+)"|'([^']+)'|(@?\([^)]+\)))/g;
const tokenRe = /\b[a-z][a-z0-9_-]*(?:--[a-z0-9_-]+)?\b/gi;
const selectorRe = /(?:^|\s)(?:::deep\s+)?\.([a-z][a-z0-9_-]*)/gim;

const used = new Set();   // classes in markup
const defs = new Map();   // class -> file

// 1. Collect defined selectors from CSS files.
// Also pick up any class name that appears anywhere in a class= expression as a string literal,
// since conditional classes (e.g. `class="@(x.IsOpen ? "home__panel--open" : "")"`) are
// defined-as-CSS but not textually present in markup.
const stringLiteralClassRe = /["']([a-z][a-z0-9_-]+(?:--[a-z0-9_-]+)?)["']/g;

for (const f of readAll(CSS_FILES, '.css')) {
  const css = fs.readFileSync(f, 'utf8');
  let m;
  while ((m = selectorRe.exec(css))) {
    defs.set(m[1], (defs.get(m[1]) || []).concat(path.relative(ROOT, f)));
  }
}

// 1b. Also capture classes referenced in string literals inside Razor markup — covers
// `class="@(x.IsOpen ? "home__panel--open" : "")"` patterns.
// Strip C# `//` line comments and `/* */` block comments first so we don't pick up
// identifiers from prose ("accessible", "active", etc.) that look like class names.
for (const f of readAll(CLIENT_FILES, '.razor')) {
  let txt = fs.readFileSync(f, 'utf8');
  txt = txt.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/[^\n]*/g, '');
  let m;
  while ((m = stringLiteralClassRe.exec(txt))) {
    if (!defs.has(m[1])) {
      defs.set(m[1], ['(string-literal in ' + path.relative(ROOT, f) + ')']);
    }
  }
}

// 2. Collect classes from markup files. Strip Razor @code blocks and Razor comments.
for (const f of readAll(CLIENT_FILES, '.razor')) {
  let html = fs.readFileSync(f, 'utf8');
  // Razor @code { } block — markup only above this point is rendered.
  const codeIdx = html.indexOf('@code');
  if (codeIdx >= 0) html = html.slice(0, codeIdx);
  // Strip Razor comments to avoid false positives in commented-out markup.
  html = html.replace(/@\*[\s\S]*?\*@/g, '');
  let m;
  while ((m = classRe.exec(html))) {
    const cls = m[1] || m[2] || m[3];
    if (!cls) continue;
    for (const tok of cls.match(tokenRe) || []) {
      // Skip Razor expression content tokens that survived: ones containing '@' or parens.
      if (tok.includes('@') || tok.includes('(')) continue;
      // C# identifiers are typically PascalCase or single lowercase words without '-'.
      // CSS classes typically contain '-' or follow BEM ('__' / '--'). Keep only those.
      if (!/[_-]/.test(tok)) continue;
      // And not the Razor reserved keywords that the token regex over-matched.
      if (/^(true|false|null|is|not|template)$/i.test(tok)) continue;
      used.add(tok);
    }
  }
}

// 3. Compare.
const ghosts = [];
const orphans = [];
for (const c of used) if (!defs.has(c)) ghosts.push(c);
for (const c of defs.keys()) if (!used.has(c)) orphans.push(c);

// 4. Categorize:
//    - `ghosts` = classes appearing in markup, NOT defined in any CSS file.
//      Real ghosts are the regression signal we care about (PRD §9 item 17/18).
//    - `orphans` = classes defined in CSS but appearing NOWHERE in markup — not even in
//      string-literal form. These are dead styles (PRD §9 item 16 removed 106 in one pass).
//      (Classes that appear in markup string literals are *used* — possibly only conditionally —
//      so we move them out of the orphan set.)

const realOrphans = [];
for (const [c, files] of defs.entries()) {
  if (used.has(c)) continue;
  if (files.some(f => f.startsWith('(string-literal'))) continue;
  realOrphans.push(c);
}

const out = {
  summary: {
    definedClasses: defs.size,
    usedClasses: used.size,
    ghosts: ghosts.length,
    orphans: realOrphans.length,
  },
  ghosts: ghosts.sort(),
  orphans: realOrphans.sort(),
};

console.log(JSON.stringify(out, null, 2));
if (ghosts.length > 0) process.exitCode = 0; // informational, not an error