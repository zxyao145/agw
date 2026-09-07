# Agw.Setup

`Agw.Setup` owns first-run administrator setup, initialization guards, and the persisted authentication state document.

## Deployment configuration

Configure deployment before starting the Server through `Database`, `Execution`, and `DistributedLock` in `appsettings.json`, environment variables, or another standard ASP.NET Core configuration provider. The priority, from lowest to highest, is built-in defaults, legacy state-file deployment values, and the standard configuration chain. Standard configuration keeps its usual order: base JSON, environment JSON, Development User Secrets when configured, environment variables, then command-line arguments.

The defaults are SQLite at `<AGW_DATA_DIR>/database/agw.db`, InProcess execution, and a lock provider inferred from the database. The base appsettings template intentionally omits the five deployment keys so its defaults cannot override an older installation. Explicit values, even values equal to the defaults, override legacy values. Overrides are per key; when changing databases, supply both Provider and ConnectionString and review Execution and DistributedLock together. See the [Deployment Guide](../../../docs/4.Deployment.md) for complete examples.

Standalone Host supports SQLite or PostgreSQL and either supported execution mode. Distributed execution requires PostgreSQL. Control Plane and Data Plane require PostgreSQL, Distributed execution, and PostgreSQL locks even before initialization. Both roles must receive the same deployment configuration; new state files do not distribute these settings. Apply coordinated deployment changes by restarting the Server. Existing Options consumers retain their update behavior, but there is no coordinated database/execution hot switch.

## Initialization

Standalone and Control Plane can start without `server-state.json`. Before initialization, normal UI requests redirect to `/setup`; APIs return 403 except Server info and health checks. The form collects only an 8–256 character administrator password and, for a domain or forwarded request, the one-time Setup Code printed at startup. Direct loopback setup is trusted.

Setup uses the same effective Database settings as normal DbContext creation. It applies pending migrations and seeds that database, hashes the password, and only then atomically persists initialization. Failure does not mark setup complete. Successful browser setup redirects to the application without another restart: execution services and the Control Plane scheduler are already registered from the deployment configuration, and background work waits for initialization.

Data Plane never exposes Setup and refuses startup until shared authentication state is initialized. Start one Control Plane, complete setup and wait for readiness, then start Data Plane and additional replicas. All replicas still share the data directory and Data Protection keys.

For unattended initialization, supply only the initial password under `Setup`, preferably through `Setup__AdminPassword` and the deployment platform's Secrets mechanism:

```json
{
  "Database": {
    "Provider": "sqlite",
    "ConnectionString": "Data Source=agw.db"
  },
  "Execution": { "Provider": "InProcess" },
  "DistributedLock": { "Provider": null, "ConnectionString": "" }
}
```

With `Setup:AdminPassword` supplied, the same initialization completes before the Server accepts traffic. Once initialized, all Setup configuration is ignored; restarts cannot overwrite an administrator password changed through Auth. `SetupCode` is never read from configuration because it protects only the browser endpoint. Old `Setup:DeploymentMode`, `Setup:Provider`, `Setup:SqlitePath`, and `Setup:Postgres*` fields are unsupported: an uninitialized Server rejects them with instructions to use standard deployment configuration. No compatibility conversion is performed.

## State persistence and compatibility

New initialization writes schema version 3 below the Agw data directory. It contains only `schemaVersion`, `isInitialized`, `passwordHash`, and `sessionVersion`. Deployment configuration and plaintext passwords are not written to new state files.

Schema versions 1 and 2 remain readable. At startup, only their actually present Database Provider/ConnectionString, Execution Provider, and DistributedLock Provider/ConnectionString values become low-priority configuration. Passwords, session versions, and initialization flags never enter IConfiguration. Authentication writes preserve legacy deployment values and their schema version so the fallback survives password changes and legacy Token removal. Do not remove the state file after moving deployment configuration: it still contains the administrator and session state.

`JsonInitializationStateStore` is the sole writer. It uses a cross-process file lock and atomic replacement. A background refresher reloads authentication state once per second; request authentication reads the last valid in-memory snapshot. This refresh does not reload deployment configuration. Readers allow Windows delete sharing required by atomic replacement.

API Token hashes and metadata live in the database `api_token` table. At startup, legacy JSON Token records are imported with their original creation time and attributed to the built-in administrator; the JSON Token section is removed only after a successful database write. Existing installations still follow the [Deployment Guide](../../../docs/4.Deployment.md) for schema upgrades; normal startup does not rerun first-run migrations.

## Authentication handoff and recovery

`Agw.Auth` owns login, Cookie and Bearer authentication, LocalTrusted, CSRF, Token management, and authorization. Password changes update the hash and invalidate existing Web sessions without altering API Token rows or deployment settings. The old `X-API-Key` configuration remains unsupported.

Stop the Server to reset a forgotten password:

```bash
agw-server auth reset-password
```
