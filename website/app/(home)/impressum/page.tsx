import type { Metadata } from 'next';
import { DisclosureRow, LegalSection, LegalShell } from '../_components/legal';

export const metadata: Metadata = {
  title: 'Impressum & Offenlegung',
  description:
    'Offenlegung gemäß § 25 Mediengesetz für die private, nicht-kommerzielle Website des Open-Source-Projekts Elarion. Media-owner disclosure under Austrian law.',
};

export default function ImpressumPage() {
  return (
    <LegalShell
      eyebrow="/// legal notice"
      title="Impressum & Offenlegung"
      subtitle="Media-owner disclosure under § 25 Mediengesetz (Austrian Media Act)."
      updated="Juli 2026"
    >
      <LegalSection title="Offenlegung gemäß § 25 Mediengesetz">
        <dl className="rounded-[4px] border border-(--line) px-5 py-2">
          <DisclosureRow label="Medieninhaber / media owner">Simon Wimmesberger</DisclosureRow>
          <DisclosureRow label="Wohnort / place of residence">Wendling, Österreich</DisclosureRow>
          <DisclosureRow label="Kontakt / contact">
            <a href="mailto:wimmesberger@gmail.com" className="text-fd-primary hover:underline">
              wimmesberger@gmail.com
            </a>
          </DisclosureRow>
          <DisclosureRow label="Grundlegende Richtung">
            Dokumentation und Information über das Open-Source-.NET-Framework Elarion —
            documentation and information about the Elarion open-source project.
          </DisclosureRow>
        </dl>
        <p>
          This is a private, non-commercial website within the meaning of § 25 Abs 5 Mediengesetz:
          it does not go beyond presenting the media owner&apos;s work on the Elarion open-source
          project and is not directed at influencing public opinion. The site carries no
          advertising, offers no paid products or services, and pursues no economic purpose. The
          service-provider information duties of § 5 E-Commerce-Gesetz (ECG) therefore do not
          apply to this site; the disclosure above is made under media law.
        </p>
      </LegalSection>

      <LegalSection title="Urheberrecht / copyright">
        <p>
          Elarion — its source code, its documentation, and the content of this site — is open
          source and published under the Apache License 2.0. The site&apos;s source lives in the
          public repository on GitHub.
        </p>
      </LegalSection>

      <LegalSection title="Externe Links / external links">
        <p>
          This site links to external websites (for example GitHub, NuGet, and npm). Those sites
          are the responsibility of their respective operators; at the time of linking no unlawful
          content was apparent. If you become aware of a problematic external link, please contact
          the address above and it will be removed.
        </p>
      </LegalSection>
    </LegalShell>
  );
}
