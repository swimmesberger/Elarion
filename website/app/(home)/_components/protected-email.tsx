'use client';

import { useEffect, useState } from 'react';

/**
 * Spam-resistant contact address. The address is assembled only at runtime in
 * the browser, so it never appears in the statically exported HTML — and the
 * fragments below are kept apart so email-pattern scanners over the JS bundle
 * find nothing to match either. No-JS visitors (and the pre-hydration HTML)
 * get a human-solvable description instead of a harvestable string.
 */
const USER = ['wim', 'mes', 'berger'].join('');
const HOST = ['gmail', 'com'].join(String.fromCharCode(46));

export function ProtectedEmail() {
  const [address, setAddress] = useState<string | null>(null);

  useEffect(() => {
    setAddress(`${USER}${String.fromCharCode(64)}${HOST}`);
  }, []);

  if (!address) {
    return (
      <span>
        {USER} — via Google&apos;s mail service (the direct link appears with JavaScript enabled)
      </span>
    );
  }

  return (
    <a href={`mailto:${address}`} className="text-fd-primary hover:underline">
      {address}
    </a>
  );
}
