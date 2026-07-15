import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';

const exportDirectory = path.resolve(process.argv[2] ?? 'out');
const configuredBasePath = (process.env.PAGES_BASE_PATH ?? '').replace(/\/$/, '');
const localOrigin = 'https://export.local';

if (!existsSync(exportDirectory)) {
  throw new Error(`Static export not found at ${exportDirectory}; run npm run build first.`);
}

function walk(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const item = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(item) : [item];
  });
}

function routeFor(file) {
  const relative = path.relative(exportDirectory, file).replaceAll(path.sep, '/');
  if (relative === 'index.html') return '/';
  return `/${relative.replace(/\/index\.html$/, '/').replace(/\.html$/, '')}`;
}

function withoutBasePath(pathname) {
  if (!configuredBasePath) return pathname;
  if (pathname === configuredBasePath) return '/';
  return pathname.startsWith(`${configuredBasePath}/`)
    ? pathname.slice(configuredBasePath.length)
    : pathname;
}

const htmlFiles = walk(exportDirectory).filter((file) => file.endsWith('.html'));
const pages = new Map();
const references = [];

for (const file of htmlFiles) {
  const route = routeFor(file);
  const html = readFileSync(file, 'utf8');
  const anchors = new Set([...html.matchAll(/\sid=["']([^"']+)["']/g)].map((match) => match[1]));
  pages.set(route, anchors);

  for (const match of html.matchAll(/\s(?:href|src)=["']([^"']+)["']/g)) {
    references.push({ from: route, raw: match[1] });
  }
}

const broken = [];

for (const reference of references) {
  if (/^(?:mailto:|tel:|data:|javascript:)/.test(reference.raw)) continue;

  let target;
  try {
    const from = `${configuredBasePath}${reference.from}`;
    target = new URL(reference.raw, `${localOrigin}${from}`);
  } catch {
    continue;
  }

  if (target.origin !== localOrigin) continue;

  const pathname = withoutBasePath(decodeURIComponent(target.pathname));
  if (pathname.startsWith('/_next/')) continue;

  const routeCandidates = pathname.endsWith('/') ? [pathname] : [pathname, `${pathname}/`];
  const route = routeCandidates.find((candidate) => pages.has(candidate));
  const exportedFile = path.join(exportDirectory, pathname.replace(/^\//, ''));

  if (!route && existsSync(exportedFile) && statSync(exportedFile).isFile()) continue;
  if (!route) {
    broken.push({ ...reference, reason: 'missing route' });
    continue;
  }

  const anchor = decodeURIComponent(target.hash.slice(1));
  if (anchor && !pages.get(route).has(anchor)) {
    broken.push({ ...reference, reason: 'missing anchor' });
  }
}

const uniqueBroken = [
  ...new Map(
    broken.map((item) => [`${item.from}\0${item.raw}\0${item.reason}`, item]),
  ).values(),
];

console.log(
  `[links] checked ${references.length} href/src references across ${htmlFiles.length} HTML files`,
);

if (uniqueBroken.length > 0) {
  for (const item of uniqueBroken) {
    console.error(`[links] ${item.from} -> ${item.raw} (${item.reason})`);
  }
  process.exitCode = 1;
} else {
  console.log('[links] no broken local routes or anchors');
}
