import { cn } from '@/lib/cn';

/**
 * Elarion wordmark: the brand "nodes wiring into handlers" symbol (echoing the
 * module → handler dispatch model) paired with the wordmark. The symbol keeps
 * the brand gradient; the wordmark uses `currentColor` so it adapts to theme.
 */
export function Logo({
  className,
  showText = true,
}: {
  className?: string;
  showText?: boolean;
}) {
  return (
    <span className={cn('inline-flex items-center gap-2.5', className)}>
      <LogoMark className="h-[1.4em] w-auto" />
      {showText ? (
        <span className="font-display text-[1.18em] font-semibold tracking-[-0.02em] text-fd-foreground">
          Elarion
        </span>
      ) : null}
    </span>
  );
}

export function LogoMark({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 232 220"
      className={className}
      role="img"
      aria-label="Elarion"
      xmlns="http://www.w3.org/2000/svg"
    >
      <defs>
        <linearGradient id="elarion-mark-g" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stopColor="#6E45F6" />
          <stop offset="52%" stopColor="#2E68FF" />
          <stop offset="100%" stopColor="#2FD6E8" />
        </linearGradient>
        <linearGradient id="elarion-mark-h" x1="0" y1="0" x2="1" y2="0">
          <stop offset="0%" stopColor="#6E45F6" />
          <stop offset="65%" stopColor="#2E68FF" />
          <stop offset="100%" stopColor="#2FD6E8" />
        </linearGradient>
      </defs>
      <circle cx="34" cy="52" r="14" fill="url(#elarion-mark-g)" />
      <circle cx="34" cy="110" r="14" fill="url(#elarion-mark-g)" opacity="0.92" />
      <circle cx="34" cy="168" r="14" fill="url(#elarion-mark-g)" opacity="0.84" />
      <rect x="64" y="38" width="126" height="28" rx="14" fill="url(#elarion-mark-h)" />
      <rect x="64" y="96" width="92" height="28" rx="14" fill="url(#elarion-mark-h)" />
      <rect x="64" y="154" width="152" height="28" rx="14" fill="url(#elarion-mark-h)" />
      <rect x="64" y="77" width="56" height="14" rx="7" fill="url(#elarion-mark-h)" opacity="0.36" />
      <rect x="64" y="135" width="78" height="14" rx="7" fill="url(#elarion-mark-h)" opacity="0.36" />
    </svg>
  );
}
