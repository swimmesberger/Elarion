---
description: 'Rules for the documentation website and the knowledge pages under docs/'
applyTo: 'docs/**,website/**'
---

# Documentation

The rules for `docs/` and `website/`. This file is their source; `AGENTS.md` points here.

## Two kinds of pages

`docs/` holds two kinds of pages, told apart by their extension; `docs/guide.md` has the full rules.

- **`.mdx`: published pages.** The website renders them; they describe how the shipped framework behaves and is
  used. Frontmatter is `title` and `description` (and `icon`); the page is listed in the `pages` of the
  `meta.json` next to it, or as `"!name"` when it is deliberately kept out of the navigation. A change to
  behavior updates its published page in the same change, and `CHANGELOG.md` when consumers notice it.
- **`.md`: repository knowledge.** Decisions, research notes, architecture, the workflow. Never published.
  Frontmatter is the Open Knowledge Format envelope with a `type`; the page is listed in the nearest
  `index.md`, and its change gets an entry in `docs/log.md` (`python3 tools/log_add.py "…"`).
- Do not turn a `.md` page into `.mdx` to publish a decision, and do not write repository knowledge into a
  published page: the site documents shipped behavior, an ADR documents why.
- `python3 tools/check_repo.py` checks both kinds.

## Documentation website

Marketing and rendered docs live in `website/`, but top-level `docs/` is the sole content source: Fumadocs
reads it through `website/source.config.ts`. Put static assets in `docs/public/`, never `website/public/`;
only `website/public/CNAME` is committed. New pages need a relevant `meta.json` entry, and new MDX components
beyond the existing standard set need registration in `website/components/mdx.tsx`. `icon:` frontmatter must
name a valid Lucide icon.

```bash
cd website && npm install && npm run build && npm run check:links
```

**Run `check:links` too, not just the build.** `deploy-docs.yml` runs it as a separate step, and it fails the
deploy on a broken internal route or anchor — which a successful `npm run build` does not catch. Link an ADR by
its absolute GitHub blob URL (`https://github.com/swimmesberger/Elarion/blob/main/docs/decisions/0013-….md`):
`docs/decisions/` is not published as a site route, so `/docs/decisions/…` is always a broken link.

The static export is `website/out`; `npm run dev` serves localhost. Pushes to `main` that touch `docs/**` or
`website/**` trigger `deploy-docs.yml`.
