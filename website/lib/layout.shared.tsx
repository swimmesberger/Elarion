import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { githubUrl } from './shared';
import { Logo } from '@/components/logo';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: <Logo className="h-7" />,
      transparentMode: 'top',
    },
    githubUrl,
    links: [
      {
        text: 'Documentation',
        url: '/docs',
        active: 'nested-url',
      },
      {
        text: 'Elarion + AI',
        url: '/ai',
        active: 'url',
      },
      {
        text: 'Philosophy',
        url: '/philosophy',
        active: 'url',
      },
      {
        text: 'Packages',
        url: '/docs/reference/packages',
      },
    ],
  };
}
