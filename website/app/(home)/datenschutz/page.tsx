import type { Metadata } from 'next';
import { DisclosureRow, LegalSection, LegalShell } from '../_components/legal';
import { ProtectedEmail } from '../_components/protected-email';

export const metadata: Metadata = {
  title: 'Datenschutzerklärung',
  description:
    'Datenschutzerklärung (privacy policy) for the Elarion website: GitHub Pages hosting, no cookies, no analytics, self-hosted fonts, and your rights under the GDPR (DSGVO) and Austrian DSG.',
};

export default function DatenschutzPage() {
  return (
    <LegalShell
      eyebrow="/// privacy"
      title="Datenschutzerklärung"
      subtitle="Privacy policy under the GDPR (DSGVO) and the Austrian Datenschutzgesetz (DSG)."
      updated="Juli 2026"
    >
      <LegalSection title="1. Verantwortlicher / controller">
        <dl className="rounded-[4px] border border-(--line) px-5 py-2">
          <DisclosureRow label="Verantwortlicher / controller">Simon Wimmesberger</DisclosureRow>
          <DisclosureRow label="Wohnort / place of residence">Wendling, Österreich</DisclosureRow>
          <DisclosureRow label="Kontakt / contact">
            <ProtectedEmail />
          </DisclosureRow>
        </dl>
        <p>
          The short version: this site sets no cookies, runs no analytics or tracking, embeds no
          third-party content at runtime, and collects no personal data itself. The only personal
          data processed is the technical minimum that any web host needs to deliver a page, plus
          whatever you choose to send if you contact us by email.
        </p>
      </LegalSection>

      <LegalSection title="2. Hosting (GitHub Pages)">
        <p>
          This site is a static website served by <strong>GitHub Pages</strong>, a service of
          GitHub, Inc. (USA; for users in the European Economic Area the contracting entity is
          GitHub B.V., Netherlands). When you visit the site, GitHub necessarily processes
          technical connection data — in particular your IP address, the requested page, browser
          type, and timestamp — to deliver the pages and to keep the service secure. We do not
          receive, store, or evaluate these server logs ourselves.
        </p>
        <p>
          Legal basis: Art 6 (1)(f) GDPR — our legitimate interest in the secure and efficient
          delivery of a static, non-commercial website. Data may be processed in the United
          States; GitHub is certified under the EU-US Data Privacy Framework and additionally
          relies on EU standard contractual clauses. Details are in the{' '}
          <a
            href="https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement"
            target="_blank"
            rel="noreferrer"
            className="text-fd-primary hover:underline"
          >
            GitHub General Privacy Statement
          </a>
          .
        </p>
      </LegalSection>

      <LegalSection title="3. Keine Cookies, kein Tracking / no cookies, no tracking">
        <p>
          This site sets <strong>no cookies</strong> and uses <strong>no analytics, advertising,
          or tracking services</strong> of any kind. Because of this, no consent banner is shown —
          none is required.
        </p>
        <p>
          One piece of information is stored locally in your browser&apos;s{' '}
          <span className="font-mono text-[0.92em]">localStorage</span>: your light/dark theme
          preference, set only when you use the theme toggle. It never leaves your device and is
          not read by anyone but this site. This is strictly functional storage at your request
          within the meaning of § 165 Abs 3 TKG 2021 and therefore requires no consent. You can
          remove it at any time by clearing your browser&apos;s site data.
        </p>
      </LegalSection>

      <LegalSection title="4. Schriften & Assets / fonts & assets">
        <p>
          All fonts, styles, scripts, and the site&apos;s search index are bundled at build time
          and served from this site&apos;s own origin. In particular, fonts are self-hosted — your
          browser makes <strong>no runtime requests to Google Fonts</strong> or any other font
          CDN. The search runs entirely in your browser against a locally served index; queries
          are not transmitted anywhere.
        </p>
      </LegalSection>

      <LegalSection title="5. Kontakt per E-Mail / contacting us">
        <p>
          If you email us, we process the data you provide (your address and the content of your
          message) to handle the enquiry — legal basis Art 6 (1)(f) GDPR, or Art 6 (1)(b) where
          your enquiry relates to entering an agreement. Messages are kept only as long as needed
          to deal with the enquiry and any follow-ups, and are then deleted.
        </p>
      </LegalSection>

      <LegalSection title="6. Externe Links / external links">
        <p>
          Links to external sites (GitHub, NuGet, npm, and others) are plain hyperlinks. No data
          is transferred to those providers until you click a link; from that point their privacy
          policies apply.
        </p>
      </LegalSection>

      <LegalSection title="7. Ihre Rechte / your rights">
        <p>
          Under Arts 15–21 GDPR you have the right to access, rectification, erasure, restriction
          of processing, data portability, and objection regarding your personal data. No
          processing on this site is based on consent, so there is no consent to withdraw; no
          automated decision-making or profiling takes place.
        </p>
        <p>
          You also have the right to lodge a complaint with the supervisory authority. In Austria
          this is the <strong>Österreichische Datenschutzbehörde</strong>, Barichgasse 40–42, 1030
          Wien,{' '}
          <a
            href="https://www.dsb.gv.at"
            target="_blank"
            rel="noreferrer"
            className="text-fd-primary hover:underline"
          >
            www.dsb.gv.at
          </a>
          .
        </p>
      </LegalSection>
    </LegalShell>
  );
}
