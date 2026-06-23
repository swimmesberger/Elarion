import { HomeLayout } from 'fumadocs-ui/layouts/home';
import { baseOptions } from '@/lib/layout.shared';

export default function Layout({ children }: LayoutProps<'/'>) {
  return (
    <HomeLayout
      {...baseOptions()}
      className="[--spacing-fd-container:80rem] [--fd-nav-height:56px]"
    >
      {children}
    </HomeLayout>
  );
}
