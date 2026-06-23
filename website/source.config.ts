import { defineConfig, defineDocs } from 'fumadocs-mdx/config';
import { metaSchema, pageSchema } from 'fumadocs-core/source/schema';

// The MDX/JSON content lives in the repository's top-level `docs/` folder so
// it stays the single source of truth (referenced from AGENTS.md and links),
// and this website renders it. Only `.mdx` files are treated as doc pages so
// plain-markdown siblings (ADRs, READMEs) under `docs/` are left untouched.
export const docs = defineDocs({
  dir: '../docs',
  docs: {
    files: ['**/*.mdx'],
    schema: pageSchema,
    postprocess: {
      includeProcessedMarkdown: true,
    },
  },
  meta: {
    files: ['**/*.json'],
    schema: metaSchema,
  },
});

export default defineConfig({
  mdxOptions: {
    // MDX options
  },
});
