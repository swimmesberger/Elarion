---
okf_version: "0.2"
---
# Project knowledge

* [How this knowledge folder is kept](guide.md) - what lives where, the two kinds of pages under docs/, and the rules for the index and the log.
* [How we work with coding agents](workflow.md) - the process from idea to merge, how much of it a change needs, practices, and which skill does what.
* [Architecture](architecture.md) - how the framework is shaped and why, with the decisions behind it.
* [Packaged modules](packaged-modules.md) - proposed direction: modules that own their data and ship as NuGet and npm packages from a module suite, composed by several applications in their own look; what Elarion must add, in order.
* [Decisions](decisions/index.md) - the architecture decision records and their format.
* [Glossary](../GLOSSARY.md) - the framework's terms with the words to avoid; at the repository root, where the domain-modeling skills look.
* [Specifications](../specs/README.md) - the spec format; one file per feature next to it.
* [Issue tracker](agents/issue-tracker.md) - where issues, specs and tickets live, for the engineering skills.
* [Domain docs](agents/domain.md) - how the engineering skills read the glossary, the decisions and the specs.
* [Sources](sources/README.md) - raw material stored as received, with a frontmatter envelope.
* [Update log](log.md) - what changed in this folder and why, newest first.

# Research notes

* [Research notes](research/README.md) - syntheses of outside material, each citing its URLs.
* [Change detection for live queries](research/2026-10-25-change-detection-for-live-queries.md) - where frameworks detect that data changed, what each layer misses, and when it publishes; input for ADR-0091.
* [Live dashboards and fan-out](research/2026-10-25-live-dashboards-and-fan-out.md) - how other systems deliver, fan out and throttle live updates over a relational database; input for ADR-0091.
* [Typst in-process concurrency](research/2026-11-18-typst-in-process-concurrency.md) - profiling of Typst compiled in-process under concurrent load, and the upstream fix; input for ADR-0094.
* [XLSX libraries for NativeAOT](research/2026-11-24-xlsx-libraries-for-native-aot.md) - which .NET spreadsheet readers and writers are AOT-compatible and maintained, and where typed row mapping breaks under AOT; input for ADR-0108.
* [CalDAV clients and .NET libraries](research/2026-11-26-caldav-servers-clients-and-dotnet.md) - what iOS, macOS, Thunderbird, DAVx5 and Outlook require of a CalDAV server, the quirks of the servers a client meets, and which .NET libraries fit NativeAOT; input for ADR-0111.
* [iCalendar in .NET](research/2026-11-26-icalendar-libraries-and-producers.md) - which .NET iCalendar libraries work under NativeAOT, how .NET resolves time zones, and what Google, Outlook, Apple and municipal calendars publish; input for ADR-0109.
* [vCard libraries and producers](research/2026-11-26-vcard-libraries-and-producers.md) - which .NET vCard libraries are AOT-safe and safe against hostile uploads, and what Android, Google, Apple, Outlook, Thunderbird and Nextcloud write into .vcf files; input for ADR-0110.
* [CardDAV clients](research/2026-11-27-carddav-clients.md) - how iOS, macOS, Thunderbird, DAVx5 and Evolution discover, sync and present CardDAV address books, which vCard version each asks for and how each treats a read-only address book; input for ADR-0113.
* [DAV conformance suites](research/2026-11-27-dav-conformance-suites.md) - the edge cases of Apple's CalDAVTester, litmus, sabre, Radicale, Xandikos, Cyrus and the DAV clients, ported as Elarion's tests; the faults they found and the answers Elarion pins where those projects disagree.

# Published documentation

* [Introduction](index.mdx) - what Elarion is; the landing page of the documentation website.
* [Why Elarion](why-elarion.mdx) - the problems the framework takes on and the bets it makes.
* [Getting started](getting-started/index.mdx) - installation, quickstart and project structure.
* [Tutorial](tutorial/index.mdx) - one application built step by step.
* [Concepts](concepts/index.mdx) - current behavior and public API of the application model: handlers, modules, authorization, validation, actors.
* [Capabilities](capabilities/index.mdx) - current behavior of the opt-in subsystems: transports, events, persistence, blobs, connections, scheduling.
* [Reference](reference/index.mdx) - attributes, diagnostics, configuration and conventions.
* [Packages](reference/packages.mdx) - the canonical grouped reference of every public package and its dependencies.
