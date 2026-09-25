# ADR-0076: Web Push is a framework package — native crypto, a fail-closed endpoint allow-list, and a pluggable browser transport

- Status: Accepted
- Date: 2026-09-25
- Related: [ADR-0043](0043-client-events.md) (the online half this complements),
  [ADR-0054](0054-device-identity-and-provisioning.md) (the core + EF store packaging this mirrors),
  [ADR-0017](0017-dependency-light-core.md) (heavy defaults live in opt-in siblings),
  [ADR-0071](0071-generator-owned-http-endpoint-binding.md) (framework `Map*` extensions use `RequestDelegate`),
  and the [Web Push](../capabilities/web-push.mdx) capability page.

## Context

Client events (ADR-0043) reach a user who has the app open. Nothing reached a user who does not — the case an
installed PWA on a phone exists for. Web Push (RFC 8030, with RFC 8291 message encryption and RFC 8292 VAPID)
closes that gap natively in every current browser, including iOS/iPadOS 16.4+ Home Screen apps.

Two consuming applications had each built it, and roughly 80% of the code was identical: VAPID key management
(configuration, then database, then generate — with a first-insert race between nodes), a subscription table
upserted by endpoint that must *reassign* a device re-subscribing under another account, a send loop that maps
urgency/TTL/topic and deletes subscriptions the push service reports gone, the subscribe endpoints, and the
browser half (key conversion, subscribe/refresh helpers, the service-worker handlers, and iOS Home Screen
detection — present in only one of the two copies). Only the recipients, the trigger, and the text differed.

## Decision

Ship Web Push as four opt-in packages in the Devices shape: `Elarion.WebPush` (core: `IWebPushSender`,
`WebPushSubscriptionService`, `IVapidKeyProvider`, the `IPushSubscriptionStore`/`IVapidKeyStore` seams and
in-memory defaults), `Elarion.WebPush.EntityFrameworkCore` (the two tables, `[GenerateElarionWebPush]`),
`Elarion.WebPush.AspNetCore` (`MapElarionWebPush`), and the npm package `@swimmesberger/elarion-webpush`.

### Encryption and signing on the platform primitives

RFC 8291 is ECDH P-256, HKDF-SHA-256, and AES-128-GCM; RFC 8292 is an ES256 JWT. .NET ships every one of
those (`ECDiffieHellman.DeriveRawSecretAgreement`, `HKDF`, `AesGcm`, `ECDsa` with IEEE P1363 output), so the
core implements the ~100 lines itself instead of taking the long-standing community `WebPush` package, which
pulls BouncyCastle and reflection-era JSON. The result is AOT-compatible and dependency-light. Correctness is
pinned to the RFC 8291 Appendix A test vector byte for byte; VAPID tokens are verified by signature, claims,
and raw-`r‖s` length. Tokens are cached per push-service origin (12-hour lifetime, renewed an hour early), so a
fan-out to a thousand FCM subscriptions signs once.

### Delivery semantics

`IWebPushSender` is best-effort and at-most-once per call, consistent with ADR-0043's "a push is a hint":
404/410 and undeliverable subscriptions (malformed keys, a disallowed endpoint) are deleted during the send;
429/5xx/timeouts/other rejections keep the subscription and count as failed; nothing is retried. A caller that
must not lose a notification drives it from its own durable record (outbox consumer, job), which already has
retry semantics — building a second retry queue into the sender would duplicate them. `WebPushMessage.Tag` is
both the notification tag and the RFC 8030 `Topic` (hashed when it is not a valid topic), so "replace, don't
stack" holds at the push service and on the device alike.

The fan-out is in-process with bounded concurrency. That covers the 1–10-node tier (ADR-0025); a
mass-notification workload replaces the `IWebPushSender` seam with a dedicated provider rather than growing
this one.

### The endpoint allow-list fails closed

The server POSTs to a URL the browser supplies, which makes the subscribe endpoint a server-side request
forgery surface for every authenticated user. Endpoints must be `https` on the known push services
(FCM, Mozilla autopush, Apple, WNS) or their subdomains, the delivery handler does not follow redirects, and
widening the list is explicit (`AllowedEndpointHosts.Add`, or `AllowAnyEndpointHost` for a trusted
population). A stored subscription whose endpoint no longer passes the list is deleted at send time.

### Keys: configuration, then store, then generate

Configured keys win so a secret store can own the private key. Otherwise the store's row is used, and on first
use a generated candidate is inserted with `ON CONFLICT DO NOTHING` and read back, so racing nodes converge on
one pair without a lock. The in-memory default loses the pair on restart, which is acceptable only because the
in-memory subscription store loses the subscriptions with it.

### Subscriptions belong to the current user; authorization stays the host's

`WebPushSubscriptionService` binds subscriptions to `ICurrentUser`, returns ordinary `Result`/`AppError`
outcomes, and unsubscribes only the caller's own row. It does not decide *who may* subscribe — the host applies
its normal policy to `MapElarionWebPush()` or its `[Require*]` attributes to its handlers. Endpoints are the
convenience; handlers delegating to the service are the first-class path for applications whose API is
`[Handler]`s, so the operations appear in the JSON-RPC schema and generated client. The framework does not ship
`[Handler]`s itself: module ownership, naming, and authorization of handlers are application decisions.

### The browser transport is pluggable

The npm helpers take a three-method `WebPushServerApi` (`getPublicKey`, `subscribe`, `unsubscribe`).
`fetchWebPushApi()` targets `MapElarionWebPush`; applications with handlers adapt their generated JSON-RPC
client in three lines. This keeps the package free of a second JSON-RPC client, per the schema/client chain
rule. The service-worker module types the worker scope structurally, so it needs no WebWorker type library
and is checked against the real `ServiceWorkerGlobalScope` in the package tests.

## Consequences

- Applications delete their copies of key management, the store, the send loop, and the browser plumbing;
  what remains is recipients, triggers, and text.
- iOS "add to Home Screen first" detection is now uniform (`pushAvailability() === 'install-first'`).
- A self-hosted or unusual push service needs one line of configuration.
- The VAPID private key sits in the application database unless configured; the capability page says so.
- Not done here: a combined "notify this user" entry point that uses client events while the user has a live
  subscriber and Web Push otherwise. It composes from the two existing seams and can be added when a second
  application asks for the same routing rule.

## Alternatives considered

- **Keep copying it per application.** The status quo; the copies had already diverged (iOS detection).
- **Depend on the community `WebPush` NuGet package.** BouncyCastle and non-AOT JSON for primitives .NET
  already provides.
- **A third-party service (FCM HTTP v1, OneSignal).** An external account and dependency for what browsers do
  natively with VAPID.
- **Client events only.** Cannot reach a closed app, which is the entire point on a phone.
- **Accept any https endpoint by default.** Convenient for self-hosted push services, but it turns every
  account into an SSRF primitive; the explicit opt-out covers the legitimate case.
