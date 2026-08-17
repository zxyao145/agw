# Agw.Setup

`Agw.Setup` owns first-run initialization and the persisted Server bootstrap document.

## Initialization

Before initialization, normal UI requests redirect to `/setup`; APIs return 403 except Server info and health checks. The setup form first selects a Standalone or Cluster deployment. Standalone supports SQLite and PostgreSQL; Cluster requires PostgreSQL and enables distributed execution after the Server restarts.

The form accepts structured database settings rather than a raw connection string. SQLite uses a Server-visible database file path. PostgreSQL uses host, port, database, username, and database password fields. The database password remains separate from the 8–256 character Agw administrator password.

Direct loopback setup is trusted. Setup through a domain or forwarded request additionally requires the one-time Setup Code printed by the Server at startup.

For unattended deployments, the same fields can be supplied under the `Setup` configuration section. If no `server-state.json` exists, startup validates this section and runs the same migration, seeding, password hashing, and atomic state write as the form before the Server starts listening. A completed state file always wins: later `Setup` configuration changes are ignored and cannot replace the administrator password or runtime setup choices.

```json
{
  "Setup": {
    "DeploymentMode": "Standalone",
    "Provider": "Sqlite",
    "SqlitePath": "agw.db",
    "AdminPassword": "replace-through-a-secret"
  }
}
```

Accepted enum values:

| Field | Values | Default |
| --- | --- | --- |
| `DeploymentMode` | `Standalone`, `Cluster` | `Standalone` |
| `Provider` | `Sqlite`, `Postgres` | `Sqlite` |

`Cluster` can only be combined with `Postgres`. Use the enum spellings above in `appsettings*.json` and environment-variable values; the user-facing labels remain SQLite and PostgreSQL.

PostgreSQL uses `PostgresHost`, `PostgresPort`, `PostgresDatabase`, `PostgresUsername`, and `PostgresPassword`. Do not commit the administrator or PostgreSQL password to `appsettings*.json`; inject them through environment variables or the deployment platform's Secret mechanism. `SetupCode` is never read from configuration because it only protects the browser setup endpoint.

Configuration-driven Cluster setup selects the distributed runtime before service registration, so the first Server starts in Cluster mode without the browser flow's extra restart. Start exactly one replica for the initial bootstrap, wait for readiness, and only then scale out.

The resulting `server-state.json` lives below the Agw data directory, not the application directory. New setup writes schema version 2 with the selected database and execution provider; Cluster also selects the PostgreSQL distributed lock provider. Existing schema version 1 documents keep their original appsettings/environment-driven execution behavior. `JsonInitializationStateStore` atomically persists initialization, runtime settings, the administrator password hash, and the Web-session version.

API Token hashes and metadata live in the database `api_token` table instead of `server-state.json`. On startup, legacy JSON Token records are imported with their original creation time, attributed to the built-in administrator because the old format had no creator, and removed from the state file only after a successful database write. See [`Agw.Auth`](../Agw.Auth/README.md) for the runtime seam.

Matching schema migrations are maintained for SQLite and PostgreSQL. Setup applies the selected provider's pending migrations before seeding and before any legacy Token import; it never marks initialization complete when migration or import fails. An already initialized installation does not repeat first-run setup during normal startup, so later schema upgrades must follow the procedure in the [Deployment Guide](../../../docs/4.Deployment.md).

## Authentication handoff

Setup hashes and persists the initial administrator password as part of the atomic bootstrap write. Login, Cookie and Bearer authentication, `LocalTrusted`, CSRF, Token management, and authorization protection are owned by `Agw.Auth`. New Token rows record the creating user and UTC creation time through the shared database audit pipeline. The old `X-API-Key` setting is intentionally not supported or migrated.

## Recovery

Stop the Server and run:

```bash
agw-server auth reset-password
```

The command updates the password hash and invalidates existing Web sessions without changing API Token rows.
