# ADR-0054: Device identity and provisioning — deferred, with the shape pre-decided

- Status: Proposed (nothing ships; build when the first consuming gateway adopts the connections stack)
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

## Decision (pre-decided shape, deferred)

A small opt-in package (working name `Elarion.Devices`) owning only the domain-neutral identity chain:

- **`IDeviceKeyStore`** seam + EF-backed default: device id → key material (raw symmetric key for the
  HMAC scheme; the self-hosted trade-off — the server must recompute MACs — is documented, as the field
  project did).
- **Pairing codes**: issue (CSPRNG, short human-typeable alphabet, TTL, single-use) and redeem
  (atomic claim → mint device id + key). The enforcement point for "capability values are never v7."
- **`HmacChallengeVerifier`**: nonce issue + constant-time response verification, shaped to be called
  from a `WebSocketConnectionHandler`/`TcpConnectionHandler` authenticator and return the ticket inputs
  (principal + `PrincipalId` = device id).
- **Device principal factory**: a `ClaimsPrincipal` with a stable claim shape so `[RequirePermission]`
  and per-dispatch authorization work unchanged for device-initiated commands.

Deliberate non-goals: device inventory/registry/management UI (domain), OTA/firmware (domain),
certificate/mTLS enrollment (a different tier — revisit only on demand).

## Consequences

- Nothing ships now. Trigger: the first real gateway port onto ADR-0053 — it needs this on day one, and
  its handshake becomes the validation exercise.
- Until then, the documented recipe is the hand-rolled chain above with the Guid-convention rules
  applied (v4/CSPRNG codes and keys, owner-checked redeem, rate-limited claim endpoint).
