import 'server-only';

import { readdirSync } from 'node:fs';
import path from 'node:path';

export type AdrCatalogEntry = {
  n: string;
  slug: string;
};

const decisionsDirectory = path.join(process.cwd(), '..', 'docs', 'decisions');

export const adrs: AdrCatalogEntry[] = readdirSync(decisionsDirectory)
  .flatMap((fileName) => {
    const match = /^(\d{4})-(.+)\.md$/.exec(fileName);
    return match ? [{ n: match[1], slug: match[2] }] : [];
  })
  .sort((left, right) => left.n.localeCompare(right.n));

export const adrCount = adrs.length;
export const latestAdrNumber = adrs.at(-1)?.n ?? '0000';
