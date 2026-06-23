import path from 'node:path';
import { createMDX } from 'fumadocs-mdx/next';

const withMDX = createMDX();

// The MDX content lives in the repository's top-level `docs/` folder, one level
// above this app. Point the bundler's workspace root at the repo root so it may
// resolve and watch `../docs` (otherwise Turbopack treats it as out-of-bounds).
const repoRoot = path.join(import.meta.dirname, '..');

// Served at the custom domain `elarion.wimmesberger.dev` (a CNAME in
// `public/CNAME`), which serves from the root — so no base path. For a GitHub
// Pages *project* URL instead (https://<user>.github.io/Elarion/), set
// PAGES_BASE_PATH="/Elarion". Exposed to the client as NEXT_PUBLIC_BASE_PATH so
// the static search client can resolve the exported index under the same prefix.
const basePath = process.env.PAGES_BASE_PATH ?? '';

/** @type {import('next').NextConfig} */
const config = {
  output: 'export',
  reactStrictMode: true,
  // Clean, Pages-friendly URLs: every route emits `<route>/index.html`.
  trailingSlash: true,
  basePath,
  images: { unoptimized: true },
  turbopack: { root: repoRoot },
  outputFileTracingRoot: repoRoot,
  env: {
    NEXT_PUBLIC_BASE_PATH: basePath,
  },
};

export default withMDX(config);
