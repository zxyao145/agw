# Agw.Setup

`Agw.Setup` owns first-run initialization and the single-administrator authentication state for Agw Server.

## Initialization

Before initialization, normal UI requests redirect to `/setup`; APIs return 403 except Server info and health checks. The setup form selects SQLite or PostgreSQL and creates an administrator password of 8–256 characters.

Direct loopback setup is trusted. Setup through a domain or forwarded request additionally requires the one-time Setup Code printed by the Server at startup.

The resulting `server-state.json` lives below the Agw data directory, not the application directory. It contains database settings, a password hash, a session version, and hashed API Token metadata. It never stores Token plaintext.

## Authentication

- Direct loopback requests with a localhost Host and no forwarding headers are locally trusted.
- Remote Web access signs in with the administrator password and receives an HttpOnly, SameSite=Strict cookie.
- Desktop, Mobile, and automation clients use named `Authorization: Bearer agw_...` Tokens.
- Unsafe Cookie/local browser requests require the `X-CSRF-TOKEN` antiforgery header.
- Token management is available only to Cookie or locally trusted administrator sessions.

The old `X-API-Key` setting is intentionally not supported or migrated.

## Recovery

Stop the Server and run:

```bash
agw-server auth reset-password
```

The command updates the password hash and invalidates existing Web sessions while preserving API Tokens.
