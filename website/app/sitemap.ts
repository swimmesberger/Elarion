import type { MetadataRoute } from 'next';
import { source } from '@/lib/source';
import { siteUrl } from '@/lib/shared';

export const dynamic = 'force-static';

const marketingRoutes = ['/', '/ai', '/philosophy', '/impressum', '/datenschutz'];

export default function sitemap(): MetadataRoute.Sitemap {
  const routes = [...marketingRoutes, ...source.getPages().map((page) => page.url)];

  return routes.map((route) => ({
    url: new URL(route, siteUrl).toString(),
  }));
}
