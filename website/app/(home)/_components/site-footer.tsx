import Link from 'next/link';
import { LogoMark } from '@/components/logo';
import { appTagline, githubUrl } from '@/lib/shared';
import { GithubIcon } from './section';

const footerColumns: { heading: string; links: { label: string; href: string }[] }[] = [
  {
    heading: 'Documentation',
    links: [
      { label: 'Introduction', href: '/docs' },
      { label: 'Getting started', href: '/docs/getting-started/installation' },
      { label: 'Tutorial', href: '/docs/tutorial' },
      { label: 'Core concepts', href: '/docs/concepts' },
      { label: 'Philosophy', href: '/philosophy' },
      { label: 'Why Elarion', href: '/docs/why-elarion' },
      { label: 'Elarion + AI', href: '/ai' },
    ],
  },
  {
    heading: 'Capabilities',
    links: [
      { label: 'Source generation', href: '/docs/concepts/source-generation' },
      { label: 'Transports', href: '/docs/concepts/transports' },
      { label: 'Authorization', href: '/docs/concepts/authorization' },
      { label: 'Events & outbox', href: '/docs/capabilities/events' },
      { label: 'Idempotency', href: '/docs/concepts/idempotency' },
      { label: 'Feature flags', href: '/docs/concepts/feature-flags' },
    ],
  },
  {
    heading: 'Reference',
    links: [
      { label: 'Packages', href: '/docs/reference/packages' },
      { label: 'Attributes', href: '/docs/reference/attributes' },
      { label: 'Diagnostics', href: '/docs/reference/diagnostics' },
      { label: 'Configuration', href: '/docs/reference/configuration' },
      { label: 'Troubleshooting', href: '/docs/reference/troubleshooting' },
    ],
  },
];

export function SiteFooter() {
  return (
    <footer className="border-t border-(--line)">
      <div className="mx-auto grid w-full max-w-[80rem] gap-12 px-5 py-14 sm:px-8 lg:grid-cols-[1.4fr_1fr_1fr_1fr] lg:px-12">
        <div>
          <LogoMark className="h-8 w-auto" />
          <p className="mt-4 max-w-xs text-sm text-fd-muted-foreground">{appTagline}</p>
          <div className="mt-5 flex items-center gap-3">
            <a
              href={githubUrl}
              target="_blank"
              rel="noreferrer"
              aria-label="GitHub"
              className="flex size-9 items-center justify-center rounded-[3px] border border-(--line) text-fd-muted-foreground transition-colors hover:border-fd-primary/50 hover:text-fd-foreground"
            >
              <GithubIcon className="size-4" />
            </a>
            <a
              href={`${githubUrl}/blob/main/CHANGELOG.md`}
              target="_blank"
              rel="noreferrer"
              className="flex h-9 items-center rounded-[3px] border border-(--line) px-3 font-mono text-xs text-fd-muted-foreground transition-colors hover:border-fd-primary/50 hover:text-fd-foreground"
            >
              Changelog
            </a>
          </div>
        </div>

        {footerColumns.map((column) => (
          <div key={column.heading}>
            <h3 className="eyebrow">{column.heading}</h3>
            <ul className="mt-4 space-y-2.5">
              {column.links.map((link) => (
                <li key={link.label}>
                  <Link
                    href={link.href}
                    className="text-sm text-fd-muted-foreground transition-colors hover:text-fd-foreground"
                  >
                    {link.label}
                  </Link>
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>

      <div className="border-t border-(--line-soft)">
        <div className="mx-auto flex w-full max-w-[80rem] flex-col items-start justify-between gap-2 px-5 py-5 font-mono text-xs text-fd-muted-foreground sm:flex-row sm:items-center sm:px-8 lg:px-12">
          <span>Open source under the Apache-2.0 License</span>
          <span className="flex flex-wrap items-center gap-x-4 gap-y-1">
            <Link href="/impressum" className="transition-colors hover:text-fd-foreground">
              Impressum
            </Link>
            <Link href="/datenschutz" className="transition-colors hover:text-fd-foreground">
              Datenschutz
            </Link>
            <span>© {new Date().getFullYear()} Simon Wimmesberger</span>
          </span>
        </div>
      </div>
    </footer>
  );
}
