# ADR-0084: Authenticating-proxy identity is an optional package on IdentityModel's ConfigurationManager

- Status: Proposed
- Date: 2026-10-10
- Related: [ADR-0009](0009-authorization-building-blocks.md) (authentication is host-owned, authorization meets it at
  `ICurrentUser`), [ADR-0017](0017-dependency-light-core.md) (heavy integrations are opt-in siblings), and the
  [Proxy identity](../capabilities/proxy-identity.mdx) capability page.

## Context

Small apps are often deployed behind an authenticating reverse proxy — Cloudflare Access, Google Cloud IAP,
oauth2-proxy, an OpenID Connect gateway — and run no sign-in flow of their own. The proxy forwards a signed token in a
vendor header or a cookie. Two independent applications on Elarion each rebuilt the same adapter around JwtBearer:
read the token from the vendor location only, validate issuer and audience with an explicit any-audience opt-in, fetch
keys from a JWKS URL or discovery, refuse to start unauthenticated outside Development, provide a Development stand-in
identity with a user switch, and read an issuer/subject/e-mail record from the principal.

Both copies hand-rolled the signing-key cache. One of them let exactly one caller fetch while every concurrent request
read the cached key list — which is empty on the first fetch — so a burst of first requests after a cold start was
refused; the same happened for a minute after any failed fetch. The other avoided the cold-start half with an explicit
warm-up call at startup. The security-relevant parts (audience fail-closed, startup refusal, which header is trusted)
were easy to get subtly wrong.

## Decision

Ship the adapter as a new optional package, `Elarion.AspNetCore.ProxyIdentity`:

- `AddElarionProxyIdentity(configuration, environment, configure?)` binds the `ProxyIdentity` section, validates it
  eagerly and throws with the offending setting named. Disabled outside Development is refused; enabled requires an
  issuer, an audience (or the explicit `AllowAnyAudience`), one key source, a token location and a subject claim.
- A JwtBearer scheme with `MapInboundClaims = false` reads the token only from the configured header/cookie (no
  fallback to `Authorization` unless configured) and requires signed, expiring tokens of the configured issuer.
- **Keys come from IdentityModel's `ConfigurationManager<OpenIdConnectConfiguration>`, not a custom store.** A bare JWKS
  URL is read by a small `IConfigurationRetriever` into the same configuration shape as discovery. An
  `IConfigurationValidator` accepts a configuration only when it names exactly the configured issuer and holds a
  signing key, so a foreign discovery document can never widen the accepted issuers (IdentityModel accepts a token whose
  issuer equals the configuration's) and an empty key set never replaces good keys.
- `ICurrentUser` is mapped to the configured subject, e-mail and role claims; the role claim is RFC 9068's `roles`.
  Identity-provider `groups` are exposed on the `ExternalIdentity` record only, because mapping them to permissions is
  application policy.
- A Development-only stand-in scheme emits the same claim shape with a distinct issuer and switches users by header or
  cookie. An optional `proxyIdentity` session-snapshot section carries the address, the logout URL and the stand-in
  flag.

## Consequences

- Concurrency, caching and refresh are the library's, and the package's tests pin the behaviour it documents: one
  fetch for any number of concurrent first requests (each waits for it), fail-closed while the endpoint is down at
  startup with recovery on the next request, background refreshes that keep serving current keys on failure or on a
  refused document.
- The library refreshes in the background after the first fetch, so **the request that first presents a token signed
  with a not-yet-fetched key is refused**; the key is accepted once the refresh lands. Proxies publish keys before they
  sign with them, so the routine refresh (one hour by default here, twelve in IdentityModel) normally wins. A blocking
  refresh exists only behind a process-wide AppContext switch, which a library must not set.
- IdentityModel's last-known-good cache is **off by default** (`LastKnownGoodLifetime = 0`, against the library's one
  hour): it would keep accepting tokens signed with a key the issuer withdrew, which is the wrong default when a key is
  withdrawn because it leaked. Apps whose issuer withdraws keys while tokens signed with them are still in use can opt in.
- Proxies that do not publish a JWKS (per-key PEM endpoints) are out of scope.

## Rejected alternatives

- **A hand-rolled key store with a refresh-on-unknown-`kid` wait.** It would make the first request after a rotation
  succeed, but it re-implements caching, rate limiting and single-flight, which is exactly where both copies had bugs.
  The one refused request per rotation is the cheaper trade-off.
- **Trusting a plain identity header** (`X-Forwarded-Email`). Anything that reaches the app without the proxy can set
  it; validating the signed token is the point.
- **Putting the adapter in `Elarion.AspNetCore`.** It pulls in JwtBearer and is relevant only to proxied deployments;
  opt-in siblings keep the host package lean.
