# Agw.Setup

`Agw.Setup` owns first-run initialization and the persisted Server bootstrap document.

## Initialization

Before initialization, normal UI requests redirect to `/setup`; APIs return 403 except Server info and health checks. The setup form selects SQLite or PostgreSQL and creates an administrator password of 8–256 characters.

Direct loopback setup is trusted. Setup through a domain or forwarded request additionally requires the one-time Setup Code printed by the Server at startup.

The resulting `server-state.json` lives below the Agw data directory, not the application directory. `JsonInitializationStateStore` is the single persistence Adapter for initialization, runtime database settings, and the [`Agw.Auth`](../Agw.Auth/README.md) authentication state seam. It preserves the existing combined schema and never stores Token plaintext.

## Authentication handoff

Setup hashes and persists the initial administrator password as part of the atomic bootstrap write. Login, Cookie and Bearer authentication, `LocalTrusted`, CSRF, Token management, and authorization protection are owned by `Agw.Auth`. The old `X-API-Key` setting is intentionally not supported or migrated.

## Recovery

Stop the Server and run:

```bash
agw-server auth reset-password
```

The command updates the password hash and invalidates existing Web sessions while preserving API Tokens.
