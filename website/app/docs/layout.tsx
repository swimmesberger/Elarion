import Link from 'next/link';
import { source } from '@/lib/source';
import { DocsLayout } from 'fumadocs-ui/layouts/docs';
import { baseOptions } from '@/lib/layout.shared';

export default function Layout({ children }: LayoutProps<'/docs'>) {
  return (
    <DocsLayout
      tree={source.getPageTree()}
      {...baseOptions()}
      sidebar={{
        // Impressum & Datenschutz must be reachable from every page (§ 25
        // MedienG / GDPR transparency); the marketing pages carry them in the
        // site footer, docs pages here.
        footer: (
          <div className="flex gap-4 px-2 pt-2 text-xs text-fd-muted-foreground">
            <Link href="/impressum" className="transition-colors hover:text-fd-foreground">
              Impressum
            </Link>
            <Link href="/datenschutz" className="transition-colors hover:text-fd-foreground">
              Datenschutz
            </Link>
          </div>
        ),
      }}
    >
      {children}
    </DocsLayout>
  );
}
