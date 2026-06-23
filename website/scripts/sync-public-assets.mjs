// Single source of truth for doc + brand static assets is the repository's
// top-level `docs/public/` folder (it lives with the MDX content and is also
// used by the root README). This website derives its served assets from there
// at build time rather than keeping committed copies:
//
//   - everything under docs/public/ is mirrored into website/public/ so any
//     asset a doc page references (e.g. /brand/x.svg) is served, and
//   - the Next favicon (app/icon.svg) is derived from the canonical brand symbol.
//
// The generated outputs are gitignored; only the committed CNAME survives in
// website/public/. Re-run automatically via the predev/prebuild/postinstall
// npm hooks, or manually with `npm run sync:assets`.
import { cpSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const websiteRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const docsPublic = join(websiteRoot, '..', 'docs', 'public');
const websitePublic = join(websiteRoot, 'public');
const faviconSource = join(docsPublic, 'brand', 'elarion-symbol-gradient-transparent.svg');
const faviconDest = join(websiteRoot, 'app', 'icon.svg');

if (!existsSync(docsPublic)) {
  console.warn(`[assets] source not found, skipping: ${docsPublic}`);
  process.exit(0);
}

// Mirror docs/public/* into website/public/* (merges; leaves CNAME in place).
mkdirSync(websitePublic, { recursive: true });
cpSync(docsPublic, websitePublic, { recursive: true });

// Derive the favicon from the same canonical symbol.
cpSync(faviconSource, faviconDest);

console.log('[assets] synced docs/public -> website/public (+ app/icon.svg)');
