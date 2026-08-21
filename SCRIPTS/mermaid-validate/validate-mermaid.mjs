#!/usr/bin/env node
// Render-validates every inline Mermaid block in the generated NET_DOCS reports.
//
// The reports embed diagrams as `<div class="mermaid">…</div>` and load Mermaid from a pinned
// CDN tag. A diagram that fails to parse renders as a red error box in the browser and nothing
// else warns, so this script extracts each block and pushes it through mmdc — the same Mermaid
// version the pages pin (enforced by the `overrides` entry in package.json), so a block that
// renders here renders there.
//
//   npm --prefix SCRIPTS/mermaid-validate install
//   npm --prefix SCRIPTS/mermaid-validate run validate            # newest docs/<YYYYMMDD>/
//   npm --prefix SCRIPTS/mermaid-validate run validate -- docs/20260821
//
// Exit code is the number of blocks that failed to render (0 = all good).

import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(here, '..', '..');

/** The Mermaid version the reports pin on the CDN. Must match `overrides.mermaid` in package.json. */
const PINNED = '10.9.1';

function newestDocsDir() {
  const docs = join(repoRoot, 'docs');
  const dated = readdirSync(docs, { withFileTypes: true })
    .filter((e) => e.isDirectory() && /^\d{8}$/.test(e.name))
    .map((e) => e.name)
    .sort();
  if (dated.length === 0) throw new Error('No docs/<YYYYMMDD>/ directory found.');
  return join(docs, dated.at(-1));
}

const target = process.argv[2] ? resolve(repoRoot, process.argv[2]) : newestDocsDir();
if (!existsSync(target)) throw new Error(`Not a directory: ${target}`);

// Guard the pin: if a report's CDN tag drifts from PINNED, this harness is validating a
// different Mermaid than the browser will run, which is worse than not validating at all.
const cdnRe = /mermaid@(\d+\.\d+\.\d+)/g;
// Both spellings: the long-form reports use <div class="mermaid">, the artifact page uses
// <pre class="mermaid">, which is what the claude.ai viewer renders natively.
const blockRe = /<(div|pre) class="mermaid">([\s\S]*?)<\/\1>/g;

const htmlFiles = readdirSync(target).filter((f) => f.endsWith('.html')).sort();
if (htmlFiles.length === 0) throw new Error(`No .html files in ${target}`);

const work = mkdtempSync(join(tmpdir(), 'mmd-'));
// Invoke mmdc's entry script through node rather than the .bin shim: on Windows, Node refuses
// to execFile a .cmd without a shell, and the failure surfaces as an empty error.
const mmdc = join(here, 'node_modules', '@mermaid-js', 'mermaid-cli', 'src', 'cli.js');
if (!existsSync(mmdc)) {
  throw new Error('mmdc not installed — run: npm --prefix SCRIPTS/mermaid-validate install');
}

// Puppeteer needs --no-sandbox in most CI containers; harmless locally.
const puppeteerCfg = join(work, 'puppeteer.json');
writeFileSync(puppeteerCfg, JSON.stringify({ args: ['--no-sandbox', '--disable-dev-shm-usage'] }));

let total = 0;
let failed = 0;
const drift = [];

for (const file of htmlFiles) {
  const html = readFileSync(join(target, file), 'utf8');

  for (const m of html.matchAll(cdnRe)) {
    if (m[1] !== PINNED) drift.push(`${file}: CDN pins mermaid@${m[1]}, validator pins ${PINNED}`);
  }

  const blocks = [...html.matchAll(blockRe)].map((m) => m[2]);
  if (blocks.length === 0) {
    console.log(`  ${file} — no inline Mermaid blocks`);
    continue;
  }

  const results = [];
  blocks.forEach((raw, i) => {
    total++;
    // The blocks are HTML — un-escape the entities Mermaid source legitimately contains.
    const src = raw
      .replace(/&lt;/g, '<').replace(/&gt;/g, '>')
      .replace(/&quot;/g, '"').replace(/&#39;/g, "'")
      .replace(/&nbsp;/g, ' ').replace(/&mdash;/g, '—').replace(/&amp;/g, '&')
      .trim();
    const stem = `${file.replace(/\.html$/, '')}-${String(i + 1).padStart(2, '0')}`;
    const inFile = join(work, `${stem}.mmd`);
    writeFileSync(inFile, src + '\n');
    try {
      execFileSync(process.execPath, [mmdc, '-i', inFile, '-o', join(work, `${stem}.svg`), '-q', '-p', puppeteerCfg], {
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      results.push({ ok: true, stem, kind: src.split('\n')[0].trim() });
    } catch (err) {
      failed++;
      // Keep the parser's own message; the puppeteer stack under it says nothing useful.
      const lines = `${err.stdout ?? ''}${err.stderr ?? ''}`.toString().trim().split('\n');
      const start = Math.max(0, lines.findIndex((l) => /Error/.test(l)));
      const detail = lines.slice(start, start + 5).join('\n      ');
      results.push({ ok: false, stem, kind: src.split('\n')[0].trim(), detail });
    }
  });

  const bad = results.filter((r) => !r.ok).length;
  console.log(`  ${file} — ${results.length - bad}/${results.length} blocks render`);
  for (const r of results.filter((x) => !x.ok)) {
    console.log(`    FAIL ${r.stem}  [${r.kind}]\n      ${r.detail}`);
  }
}

console.log('');
for (const d of drift) console.log(`  PIN DRIFT  ${d}`);
console.log(`  ${total - failed}/${total} Mermaid blocks render under mermaid@${PINNED}`);

rmSync(work, { recursive: true, force: true });
process.exit(failed + drift.length);
