# Agw.Setup

`Agw.Setup` owns first-run administrator setup, initialization guards, and database-backed initialization snapshots.

## Deployment configuration

Configure deployment before starting the Server through `Database`, `Execution`, and `DistributedLock` in `appsettings.json`, environment variables, or another standard ASP.NET Core configuration provider. The priority, from lowest to highest, is built-in defaults and the standard configuration chain. Standard configuration keeps its usual order: base JSON, environment JSON, Development User Secrets when configured, environment variables, then command-line arguments.

The defaults are SQLite at `<AgwDataDir>/database/agw.db`, InProcess execution, and a lock provider inferred from the database. The base appsettings template omits the five deployment defaults; standard configuration overrides built-in defaults. Overrides are per key; when changing databases, supply both Provider and ConnectionString and review Execution and DistributedLock together. See the [Deployment Guide](../../../docs/4.Deployment.md) for complete examples.

Standalone Host supports SQLite or PostgreSQL and either supported execution mode. Distributed execution requires PostgreSQL. Control Plane and Data Plane require PostgreSQL, Distributed execution, and PostgreSQL locks even before initialization. Both roles must receive the same deployment configuration; authentication state does not distribute these settings. Apply coordinated deployment changes by restarting the Server. Existing Options consumers retain their update behavior, but there is no coordinated database/execution hot switch.

## Initialization

Standalone and Control Plane can start without `server-state.json`. Before initialization, normal UI requests redirect to `/setup`; APIs return 403 except Server info and health checks. The form collects only an 8–256 character administrator password and, for a domain or forwarded request, the one-time Setup Code printed at startup. Direct loopback setup is trusted.

Setup uses the same effective Database settings as normal DbContext creation. It applies pending migrations and seeds that database, hashes the password, and only then inserts the administrator record into the global `auth` group of `setting`. Failure does not mark setup complete. Successful browser setup redirects to the application without another restart: execution services and the Control Plane scheduler are already registered from the deployment configuration, and background work waits for initialization.

Data Plane never exposes Setup. Its readiness and protected execution wait until the database administrator record exists. Start one Control Plane, complete setup and wait for readiness, then start Data Plane and additional replicas. All replicas still share the data directory and Data Protection keys.

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

## State persistence

The single global `auth` configuration row in `setting` stores the administrator password hash, session version, initialization timestamp, and audit metadata. Its presence indicates completed initialization. Password changes use optimistic concurrency; concurrent setup cannot overwrite an existing record.

`DatabaseInitializationStateStore` refreshes authentication snapshots from the database once per second. Failed refreshes discard credentials rather than trusting a stale password or session version. Host startup reads the database before accepting traffic. No authentication values enter IConfiguration.

API Token hashes and metadata remain in `api_token`. File persistence, legacy configuration fallback, and legacy JSON Token import are removed. See the [Deployment Guide](../../../docs/4.Deployment.md) for the one-time upgrade procedure.

## Authentication handoff and recovery

`Agw.Auth` owns login, Cookie and Bearer authentication, LocalTrusted, CSRF, Token management, and authorization. Password changes update the hash and invalidate existing Web sessions without altering API Token rows or deployment settings. The old `X-API-Key` configuration remains unsupported.

Stop the Server to reset a forgotten password:

```bash
agw-server auth reset-password
```
