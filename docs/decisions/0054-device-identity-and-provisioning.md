# ADR-0054: Device identity and provisioning

- Status: Accepted (2026-07-13 — originally Proposed as deferred; implemented as `Elarion.Devices` +
  `Elarion.Devices.EntityFrameworkCore` ahead of the first gateway port)
- Date: 2026-07-13
- Related: [ADR-0053](0053-bidirectional-client-connections.md) (the handshake seam this mounts on),
  [ADR-0013](0013-resource-and-data-level-authorization.md) (grants stay the human-authorization tier),
  the AGENTS.md Guid convention (capability values are v4/CSPRNG, never v7).

## Context

Every device gateway examined in the field hand-rolls the same identity chain, and one consuming
project's architecture review explicitly flagged it as an Elarion gap: **pairing/provisioning** (a
short-lived, single-use, human-typeable claim code redeemed once by the device for its identity + key,
over a rate-limited anonymous endpoint), **key storage** (a per-device symmetric key, CSPRNG-generated
server-side), and **connect-time authentication** (an in-socket HMAC challenge/response producing the
device principal). Hand-rolling this is exactly where mistakes are security-relevant: nonce reuse,
non-constant-time comparison, guessable codes, keys minted from the wrong randomness.

The connections stack (ADR-0053) created the natural mounting point: the adapter handshake seam
(`AuthenticateAsync` → `ClientConnectionTicket`) is where such a verifier plugs in with three lines.

## Decision

A small opt-in package pair owning only the domain-neutral identity chain — `Elarion.Devices` (the
chain; BCL crypto only, `IsAotCompatible`) and `Elarion.Devices.EntityFrameworkCore` (the durable
stores + bundled `[GenerateElarionDeviceIdentity]` generator, ELDEV001):

- **`IDeviceKeyStore`** seam + EF-backed default (`elarion_device_keys`): device id → key material
  (raw symmetric key for the HMAC scheme; the self-hosted trade-off — the server must recompute
  MACs — is documented, as the field project did). In-memory sibling is an explicit opt-in
  (`AddElarionInMemoryDeviceIdentityStores`) — durable identity never gets a silent volatile default.
- **Pairing codes** (`IDevicePairingService` + the `IPairingCodeStore` seam): issue (CSPRNG over a
  Crockford-style human-typeable alphabet — validated normalization-stable and duplicate-free so
  issued codes are always redeemable and unbiased —, TTL, single-use; the device id is pre-assigned
  at issue so the issuer can attach it to domain state) and redeem (atomic single-winner claim — one
  `DELETE … RETURNING` on the EF store — → mint key; unknown/expired/used are indistinguishable).
  **Re-pairing rotates**: a code issued for an already-provisioned device id replaces its key at
  redeem — issuing that code is the re-key authorization (device reset, lost key), so the flow needs
  no separate rotate API. Codes are stored **hashed** (SHA-256), so a leaked table yields nothing
  redeemable. The enforcement point for "capability values are never v7."
- **`HmacChallengeVerifier`**: nonce issue + constant-time response verification
  (`CryptographicOperations.FixedTimeEquals`; unknown ids pay the same MAC cost as known ones), shaped
  to be called from a `WebSocketConnectionHandler`/`TcpConnectionHandler` authenticator and return the
  ticket inputs (principal + `PrincipalId` = device id).
- **Device principal factory** (`DevicePrincipal`): a `ClaimsPrincipal` with a stable claim shape
  (`elarion:device` + name identifier = device id, authentication type `ElarionDevice`) so
  `[RequirePermission]` and per-dispatch authorization work unchanged for device-initiated commands.

Deliberate non-goals: device inventory/registry/management UI (domain), OTA/firmware (domain),
certificate/mTLS enrollment (a different tier — revisit only on demand). The rate-limited anonymous
redeem endpoint stays app-owned (hosting/throttling policy is the app's).

## Consequences

- Hand-rolling the security-relevant chain (nonce reuse, non-constant-time comparison, guessable
  codes, keys minted from the wrong randomness) is replaced by three lines in a connection
  authenticator: verify → build the ticket → return.
- The first gateway port onto ADR-0053 becomes the validation exercise for the shipped shape; gaps it
  surfaces evolve this package rather than re-opening a hand-rolled chain.
- Stores follow the framework store conventions: singleton over a fresh DI scope per operation
  (handshakes and redeem endpoints run outside handler scopes), change-tracker-free writes, tables
  mapped through the EF generator's per-feature seam.
