# ADR 0024: Server Identity and Device Registration

**Status:** Accepted  
**Date:** 2026-08-03  
**Supersedes:** None  
**Superseded by:** None  
**Related decisions:** D-017 (Server identity/authentication)

## Context

The optional HackerOs server requires an authentication model that:
1. Allows local-only users to remain fully offline without any server account.
2. Supports multi-device sync without shipping signing secrets to the browser.
3. Enables instant token revocation without waiting for JWT expiry.
4. Avoids trusting any client-side authorization claims.

## Decision

### Authentication Flow
- Account creation stores a **server-side PBKDF2-SHA256 re-hash** of the client's pre-computed hash. The client hashes the password before transmission; the server applies a second PBKDF2 round. This prevents hash-as-password attacks.
- Access tokens are **opaque random 32-byte strings**, hashed (SHA-256) and stored in an in-memory cache keyed by hash. They are **never JWTs**; the server cannot be tricked by a modified payload.
- Refresh tokens are **opaque 64-byte strings**, SHA-256 hashed and stored in the database, bound to a device and account.
- Access tokens expire after **15 minutes**. Refresh tokens expire after **30 days** and are **rotated on every use** (the old token is immediately invalidated).
- Every device has a stable **device fingerprint** (browser-generated UUID or agent hash). The server validates fingerprint on login and refresh to detect cross-device token sharing.
- The server validates every request against its own stored data. **No client-supplied claims are trusted.**

### Device Management
- Every account can register multiple devices.
- Devices can be revoked by the account holder. Revocation immediately invalidates all in-memory access tokens and database refresh tokens for that device.

### Local-Only Users
- Users who never create a server account continue to work entirely offline.
- No server account is required for any local functionality.

## Consequences

- **Positive:** Instant revocation, no public key distribution, no SSRF via forged JWTs.
- **Positive:** Local-only users are fully unaffected.
- **Negative:** Access token validation requires an in-memory cache; the cache is not shared across server replicas. Multi-replica deployments must use sticky sessions or a shared Redis cache.
- **Mitigation:** The 15-minute TTL limits the blast radius of a cache miss on failover.

## Rejected Alternatives

- **JWT with asymmetric signing:** Increases deployment complexity (key rotation), does not enable instant revocation, and ships a public key to the client.
- **Session cookies:** Require CSRF protection and complicate the PWA/offline model.
