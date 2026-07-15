export const appName = 'Elarion';
export const appTagline = 'Application framework for .NET';
export const appDescription =
  'Elarion is a source-generated .NET application framework for modular handlers, HTTP, JSON-RPC, MCP, background work, realtime, and PostgreSQL-backed infrastructure.';

export const docsRoute = '/docs';
export const docsImageRoute = '/og/docs';
export const docsContentRoute = '/llms.mdx/docs';

// Where the MDX content lives in the repository, used to build GitHub edit links.
export const docsContentPath = 'docs';

// Mirrors `basePath` in next.config.mjs (injected via NEXT_PUBLIC_BASE_PATH).
// Used by client code that must resolve URLs Next does not prefix automatically,
// such as the statically-exported Orama search index.
export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? '';

export const gitConfig = {
  user: 'swimmesberger',
  repo: 'Elarion',
  branch: 'main',
};

export const githubUrl = `https://github.com/${gitConfig.user}/${gitConfig.repo}`;

// Public origin the site is served from. Used for absolute metadata/OG URLs.
export const siteUrl = process.env.NEXT_PUBLIC_SITE_URL ?? 'https://elarion.wimmesberger.dev';
