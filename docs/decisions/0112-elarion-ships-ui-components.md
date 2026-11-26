---
type: Architecture Decision Record
title: "ADR-0112: Elarion ships UI components; the application keeps the look"
description: "Proposal: a dependency-free headless package binds the framework's contracts (lists, commands, the shell contract) and an opt-in styled package ships React components on Tailwind CSS v4 and Radix with shadcn/ui's token names; the application owns token values, the shell's layout and its brand."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T14:00:00Z }
status: draft
---
# ADR-0112: Elarion ships UI components; the application keeps the look

- Status: Proposed
- Date: 2026-11-26
- Amends: [ADR-0032](0032-frontend-contribution-model.md) (its non-goal "a UI kit", and "point payload shapes stay
  app-owned" for the shell's own points)
- Related: [packaged modules](../packaged-modules.md) (the concept this serves),
  [ADR-0030](0030-client-capability-bootstrap.md), [ADR-0043](0043-client-events.md),
  [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md),
  [ADR-0082](0082-requiredness-comes-from-nullability-in-both-directions.md),
  [ADR-0100](0100-the-schema-exports-constants-presets-and-limits.md),
  [ADR-0101](0101-a-decision-required-answer.md), [ADR-0104](0104-previewable-commands.md)

## Context

ADR-0032 drew the frontend line at mechanics: Elarion ships extension points, manifests, `when` clauses and the
capability vocabulary; the application owns every payload, its shell and every component. "A UI kit" was a
non-goal, and a framework navigation item was rejected as "a five-line interface with no drift cost". The reason
was sound then: the look of an application is the application's, and nothing tied components to the framework.

Two things have changed since.

**The schema now describes everything a list or a command needs.** The generated client exports the `Page<T>`
envelope, the sort keys a list allows and its default (`rpcSortKeys`), named request presets (`rpcPresets`),
typed limits (`rpcLimits`), each method's requirements (`rpcMethodInfo`, `canCall`), declared errors with stable
codes, decision-required questions and answers, previewable commands, Zod schemas for every request, and client
events that say when to query again. Every application turns these into the same things by hand: a table that
pages and sorts on the server, filter chips, a form that puts a validation error on its field and marks required
members, a confirmation, a decision dialog, a preview before a commit, an error state per error kind. The
binding is the same each time, it is where list pages and forms of different applications drift apart, and it
falls behind whenever Elarion adds a contract. Elarion's own Billing sample shows the pattern: it carries
hand-copied shadcn/ui components (`button`, `dialog`, `input`, `table`, `sonner`) and writes TanStack Query hooks
per module.

**Modules will run in several applications.** The [packaged modules](../packaged-modules.md) concept ships a
module's user interface as an npm package that several applications compose. Such a module needs components and
a shell it can count on in every application. If each application or each module suite owns them, a module runs
only where its kit exists, and the binding to the framework's contracts is written once per kit.

The question is where to draw the line between framework and application. A data table's behaviour (what it
sorts, how it pages, which actions it offers, when it reloads) follows from the framework's contracts. Its look
(colours, type, radius, density) does not.

## Decision

### 1. Elarion ships behaviour and structure; the application owns the look

Elarion's UI packages decide how a list, a form, a dialog and the shell's places behave and which parts they
have. The application decides how they look:

| Elarion owns | The application owns |
|---|---|
| the behaviour of lists, forms, dialogs and states, bound to the framework's contracts | the values of the design tokens: colours, radius, fonts, spacing scale (density), light and dark |
| the components' structure, accessibility and keyboard paths | the shell's layout: sidebar or top bar, where the user menu sits, the phone layout |
| the names of the design tokens | its brand: logo, illustrations, wording |
| the shell contract: the shell's extension points and the services modules may use | its own components, beside Elarion's, for its own pages |
| English default texts | other locales and overrides of any text |

Elarion ships no brand, no shell layout, and no component for an application domain (no "address field", no
"invoice table").

### 2. Two npm packages, headless and styled

**`@swimmesberger/elarion-ui-headless`** has no markup, no styles and no runtime dependencies. Its framework-free
entry holds the state of the framework's contracts:

- a **list** over one method: page and page size within the method's limits, sort from its sort keys, its
  presets, and a query again when one of the client events the list names arrives;
- a **command**: pending, success, an `AppError` by kind and code, validation errors mapped to member paths, a
  decision-required question and its answer, a preview and the commit of what was previewed;
- **action gating** from the method's requirements (`canCall`).

Its `/react` sub-export holds the hooks over that state, built on TanStack Query (a peer dependency, one
`QueryClient` per application), and the shell contract (section 4). Hook names are settled with the
implementation; `useList`, `useCommand` and `useShell` stand for them here.

**`@swimmesberger/elarion-ui`** is opt-in and holds styled React components on Tailwind CSS v4 and Radix,
following shadcn/ui's conventions: the same component APIs, variants with `class-variance-authority`, a
`className` on every part merged with `tailwind-merge`. It ships three things:

- **Primitives:** button, input, textarea, select, checkbox, switch, label, dialog, alert dialog, popover,
  dropdown menu, tabs, tooltip, badge, card, skeleton, table. Elarion ships them because its bound components are
  built from them, and because a module that renders its own buttons must render the same ones.
- **Components bound to the framework's contracts:**
  - a data table over a list method: server-side paging, sorting from the sort keys, preset chips, row actions
    gated by `canCall`, selection, empty, loading and error states, a query again on client events (on
    TanStack Table);
  - a command form and its fields: client-side validation from the generated request schema, server validation
    errors on their fields, required marks from nullability, the submit state (on TanStack Form);
  - confirmation, decision and preview dialogs;
  - an error state per `AppError` kind, an empty state, a page header.
- **`theme.css`:** neutral default values for every token and the `@theme inline` mapping to Tailwind utilities.

```tsx
const columns = defineColumns("invoices.list", (c) => [
  c.text("number", { label: "Number" }),     // sortable when "number" is one of the method's sort keys
  c.text("clientName", { label: "Client" }),
  c.money("total", { label: "Total" }),
])

<DataTable method="invoices.list" columns={columns} rowActions={invoiceActions} requeryOn={["invoices.changed"]} />
```

### 3. Token names are shadcn/ui's

The components read shadcn/ui's semantic CSS custom properties: `--background`, `--foreground`, `--card`,
`--popover`, `--primary`, `--secondary`, `--muted`, `--accent`, `--destructive`, `--border`, `--input`,
`--ring`, `--radius`, `--chart-*` and `--sidebar-*`, each with its `-foreground` pair where shadcn/ui has one.
Density follows Tailwind v4's `--spacing`, from which every spacing utility derives. An application already
themed for shadcn/ui themes Elarion's components with no change; any other application sets these variables in
one stylesheet:

```css
@import "tailwindcss";
@import "@swimmesberger/elarion-ui/theme.css";
@source "../node_modules/@swimmesberger/elarion-ui";

:root {
  --primary: oklch(0.55 0.18 250);
  --radius: 0.375rem;
  --spacing: 0.225rem; /* a denser application */
}
```

### 4. The shell contract becomes Elarion's

The headless package's `/react` sub-export holds the shell contract: what every application's shell gives a user
interface module.

- **The shell's extension points**, with payloads Elarion owns: navigation (sections and items: label key, icon,
  target, order, phone tab rank), settings sections, dashboard widgets, search providers, quick actions.
- **Services:** navigation (a link component and `navigate`), the capability reader, notifications,
  confirmation, labels and formatting (locale, time zone, numbers, dates), the transport for generated clients,
  and an error boundary.
- The application's shell implements it once with `<ShellProvider>`. Elarion's components use it too: a row
  link navigates through it, a failed command notifies through it, every text comes from its labels.

This amends ADR-0032 for the shell's own points only. Extension points an application or a module declares for
its own places keep payloads their owner defines.

### 5. Texts

Every text a component shows comes from the shell's label service, under the namespace `elarion`. Elarion ships
English defaults; an application supplies its other locales and may override any key.

### 6. A package, not a copy

Applications and modules import the components; they are not copied into the application with the shadcn/ui
command line. A fix reaches every application with a version, and a packaged module can import what it renders.
Both packages are versioned with every other Elarion package.

### 7. What may enter the styled package

A component enters `@swimmesberger/elarion-ui` when it is a primitive that a bound component uses, or when it
binds a framework contract. Anything else (a date range picker without a contract behind it, a rich-text editor,
a chart) stays with the application until the framework grows a contract it would bind.

### 8. React only, for the styled package

The styled package is React. The headless package's framework-free entry serves any framework; an `/angular`
sub-export follows when an application asks for it, as for `@swimmesberger/elarion-contributions`.

## Consequences

**Positive**

- A list pages, sorts, filters, gates its actions and reloads the same way in every application, and a form
  shows errors the same way, because the behaviour is in one package.
- A new framework contract (an error kind, a limit, a kind of decision) reaches every user interface in the
  release that adds it.
- Packaged modules get components and a shell they can count on in every application.
- An application themed for shadcn/ui adopts the components without restyling.
- The Billing sample loses its hand-copied components and its per-module query plumbing.

**Negative, accepted**

- Elarion takes a stance on React, Tailwind CSS v4, Radix and the TanStack libraries. It is the stack the
  documentation already recommends, the styled package is opt-in, and the headless package carries none of it.
- The maintained surface grows: accessibility, keyboard paths, upgrades of Radix, Tailwind CSS and TanStack, and
  visual regressions. The rule of section 7 keeps the surface to what the framework's contracts justify.
- An application that wants a look the tokens and `className` cannot reach composes its own components from the
  headless hooks. It keeps the behaviour and loses the shared markup.
- On acceptance, these change with it: `AGENTS.md` ("UI frameworks … belong in consuming repositories" gains an
  exception for these two packages), `.github/instructions/framework.instructions.md` ("not a UI kit"), ADR-0032's
  non-goals, the package reference, and the frontend pages.

## Rejected alternatives

- **Headless only.** Elarion would bind the contracts, and each application or module suite would style them.
  This keeps Elarion out of how things look, but the tokens already keep it out of that. Every kit would rebuild
  the same table, form and dialogs, and a packaged module could not count on any of them.
- **A design system owned by each module suite** (the first draft of the packaged-modules concept). It binds the
  framework's contracts once per suite and falls behind every Elarion release that adds one.
- **A shadcn/ui registry, components copied in by the command line.** It fits shadcn/ui's own model and lets an
  application edit a component, but it forks every fix once per application, and a packaged module cannot import
  code that each application holds its own copy of. An application that must change a component's internals
  composes its own from the headless hooks.
- **Token names of Elarion's own.** Every application already themed for shadcn/ui would have to map them.
- **Another component library (MUI, Mantine, Chakra UI).** Each brings its own styling system and a theme object
  in JavaScript, which fights Tailwind CSS and is not the stack the documentation recommends.
- **React Hook Form instead of TanStack Form.** It is shadcn/ui's default and widely used. TanStack Form validates
  with Standard Schema, which the generated Zod schemas are, and belongs to the same family as the router, the
  table and the query library the stack already uses. Revisable.

## Implementation

Sample-first, as ADR-0032: each step lands in the Billing sample, then in the package.

1. The headless package: list and command state with unit tests, the `/react` hooks on TanStack Query, the shell
   contract.
2. The styled package: `theme.css` and the primitives, replacing the sample's copied components.
3. The data table over list state; the sample's list pages move to it.
4. The command form, its fields, and the confirmation, decision and preview dialogs.
5. A published page for the components, both packages in the package reference, `CHANGELOG.md`, and the changes
   listed under Consequences.

Each component gets tests with Testing Library and axe-core, and its keyboard path is tested.
