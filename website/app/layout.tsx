import { Bricolage_Grotesque, Hanken_Grotesk, JetBrains_Mono } from 'next/font/google';
import type { Metadata, Viewport } from 'next';
import { Provider } from '@/components/provider';
import { appDescription, appName, appTagline, siteUrl } from '@/lib/shared';
import './global.css';

const display = Bricolage_Grotesque({
  subsets: ['latin'],
  variable: '--ff-display',
  display: 'swap',
  weight: ['400', '500', '600', '700', '800'],
});

const sans = Hanken_Grotesk({
  subsets: ['latin'],
  variable: '--ff-body',
  display: 'swap',
});

const mono = JetBrains_Mono({
  subsets: ['latin'],
  variable: '--ff-mono',
  display: 'swap',
});

export const metadata: Metadata = {
  metadataBase: new URL(siteUrl),
  title: {
    default: `${appName} — ${appTagline}`,
    template: `%s · ${appName}`,
  },
  description: appDescription,
  applicationName: appName,
  authors: [{ name: 'Simon Wimmesberger' }],
  keywords: [
    '.NET',
    'application framework',
    'source generators',
    'JSON-RPC',
    'MCP',
    'Model Context Protocol',
    'Entity Framework Core',
    'modular monolith',
    'AOT',
  ],
  openGraph: {
    type: 'website',
    siteName: appName,
    title: `${appName} — ${appTagline}`,
    description: appDescription,
  },
  twitter: {
    card: 'summary_large_image',
    title: `${appName} — ${appTagline}`,
    description: appDescription,
  },
};

export const viewport: Viewport = {
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#f7f8fb' },
    { media: '(prefers-color-scheme: dark)', color: '#070d1f' },
  ],
};

export default function Layout({ children }: LayoutProps<'/'>) {
  return (
    <html
      lang="en"
      className={`${display.variable} ${sans.variable} ${mono.variable}`}
      suppressHydrationWarning
    >
      <body className="flex flex-col min-h-screen font-sans antialiased">
        {/* Progressive enhancement: tag the document before paint so scroll-reveal
            sections only start hidden when JS can animate them back in. */}
        <script
          dangerouslySetInnerHTML={{
            __html: "document.documentElement.classList.add('js')",
          }}
        />
        <Provider>{children}</Provider>
      </body>
    </html>
  );
}
