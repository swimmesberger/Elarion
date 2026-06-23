# Elarion website

Marketing landing page + documentation for Elarion, built with
[Next.js](https://nextjs.org) and [Fumadocs](https://fumadocs.dev) and deployed
as a static site to GitHub Pages.

The MDX content is **not** stored here — it lives in the repository's top-level
[`docs/`](../docs) folder, which stays the single source of truth. This app
points Fumadocs at `../docs` (see [`source.config.ts`](source.config.ts)) and
renders it alongside the custom landing page in [`app/(home)`](<app/(home)>).

Static assets follow the same rule. The single home for doc + brand assets is
[`docs/public/`](../docs/public) (also used by the root README).
[`scripts/sync-public-assets.mjs`](scripts/sync-public-assets.mjs) — run by the
`predev`/`prebuild`/`postinstall` hooks — mirrors it into `public/` and derives the
favicon `app/icon.svg`. Those are **generated and gitignored**; only `public/CNAME`
is committed. Add new images to `docs/public/`, not here.

## Develop

```bash
cd website
npm install
npm run dev      # http://localhost:3000
```

## Build & preview the static export

```bash
npm run build    # static export to ./out
npm start        # serve ./out locally
```

## Deployment

Pushing changes under `website/**` or `docs/**` to `main` triggers
[`.github/workflows/deploy-docs.yml`](../.github/workflows/deploy-docs.yml),
which builds the static export and publishes it to GitHub Pages.

Enable it once in **Settings → Pages → Build and deployment → Source → GitHub
Actions**.

### Custom domain

The site is served at **`elarion.wimmesberger.dev`** (a subdomain that serves
from the root), so it builds with **no base path**. The domain is pinned by
[`public/CNAME`](public/CNAME), which the export copies to `out/CNAME` so it
survives every deploy.

You still need a DNS record at your provider:

```
elarion  CNAME  swimmesberger.github.io.
```

To switch to the GitHub Pages **project** URL instead
(`https://swimmesberger.github.io/Elarion/`), remove `public/CNAME` and build
with `PAGES_BASE_PATH="/Elarion"` (set the same env var on the workflow's build
step).
