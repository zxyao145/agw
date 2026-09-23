---
title: "Configuration and authentication"
description: "Understand configuration precedence, deployment defaults, and API Key behavior."
weight: 30
lastmod: 2026-09-23
translationKey: docs/operations/configuration
---

This page covers deployment settings consumed by AGW Server and logging settings in the Host template. Configure models, Agents, Projects, and integration accounts through the management UI as described in their guides. Choose your deployment mode first, then apply changes and restart the relevant Hosts.

## Start with the settings relevant to your task

For an initial local setup, keep defaults and complete initialization. Before relying on the service, locate its database and data directory and follow the [backup guide]({{< relref "/docs/operations/backup" >}}).

For remote access, check listening addresses, allowed origins, proxy trust, and authentication first. For split deployment, begin with the required shared configuration. Polling and batch settings are tuning references; they do not all need changes during initial setup.

## Configuration and precedence

General precedence, from low to high: built-in defaults → `appsettings.json` → environment-specific JSON → Development Secrets → environment variables → command line. Files reside in the Server executable directory, and overrides apply per key. Serilog has a separate configuration reader described below.

Colons denote hierarchy: `Database:Provider` becomes nested JSON, `Database__Provider` in the environment, or `--Database:Provider postgres` on the command line. Use `true` and `false` for booleans and the listed names for enums.

“Template” means the repository Host’s `appsettings.json`. “Omitted” means the key is absent from all configuration sources. Differences are marked explicitly.

## Choose settings for your deployment

Deployment topology determines which Server programs you start. Execution mode determines how tasks run. Choose both explicitly.

| Group | When to use it |
| --- | --- |
| Common settings | Every deployment: endpoints, directories, database, authentication, and logs |
| Standalone deployment | One Server manages, schedules, and executes; defaults to SQLite and InProcess |
| Split Control/Data Plane deployment | Separate management and execution; requires PostgreSQL and Distributed |
| Distributed execution tuning | Any deployment using Distributed, including Standalone when explicitly enabled |

## Standalone deployment

For local use or a single Server, start with this default combination. These keys can usually be left unset:

| Full key | Default | Meaning |
| --- | --- | --- |
| `Database:Provider` | `sqlite` | Use a local database file |
| `Database:ConnectionString` | `Data Source=agw.db` | SQLite file location |
| `Execution:Provider` | `InProcess` | Run tasks directly in the current Server |
| `DistributedLock:Provider` | Unset | Automatically use an in-process lock with SQLite |
| `DistributedLock:ConnectionString` | Empty | In-process locks need no database connection |

Standalone can also use PostgreSQL while retaining InProcess execution. If you choose Distributed, satisfy the PostgreSQL database and lock requirements below and configure distributed execution accordingly. See [Standalone and Docker]({{< relref "/docs/operations/standalone" >}}).

## Split Control/Data Plane deployment

Both planes must use the same application database and the combination below. Initialize Control Plane before starting Data Plane.

| Full key | Required setting | Where to configure |
| --- | --- | --- |
| `Database:Provider` | `postgres` | Both planes |
| `Database:ConnectionString` | The same PostgreSQL database | Both planes |
| `Execution:Provider` | `Distributed` | Both planes |
| `DistributedLock:Provider` | `postgres`, or omit to follow the database | Both planes |
| `DistributedLock:ConnectionString` | Empty to reuse the database connection, or the same lock service | Consistent across both planes |

Apply common settings according to each Server’s responsibilities:

- **Control Plane**: configure initial setup, management URLs, and public integration OAuth URLs.
- **Data Plane**: prepare Agent CLIs, Shell, workspaces, and files. Worker concurrency and polling settings affect execution here.
- **Both planes**: check listening URLs, client origins, proxies, logs, and monitoring. Nodes decrypting shared data need matching encryption keys. All execution nodes must be able to access the captured task directories.

### Execution mode

All fields below use the prefix `Execution:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `Provider` | InProcess | `InProcess`: execute in the current process. `Distributed`: coordinate durable execution through PostgreSQL, with workers claiming work. Distributed requires PostgreSQL for both the database and locks. |

The executable determines the Host role: Standalone combines both planes, Control Plane manages and schedules, and Data Plane executes. Both split Hosts require PostgreSQL, Distributed execution, and PostgreSQL locks. This setting does not change one Host executable into another role.

### Distributed locks

All fields below use the prefix `DistributedLock:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `Provider` | Unspecified; follows the database | `inmemory`: coordinate within one process, for single-node use. `postgres`: coordinate multiple nodes through PostgreSQL. When omitted or null, SQLite selects inmemory and PostgreSQL selects postgres. |
| `ConnectionString` | Empty | Connection string for PostgreSQL locks. An empty value reuses `Database:ConnectionString`. In-memory locks do not use a connection string. |



## Distributed execution tuning

These settings apply to Distributed execution, including split deployments and Standalone with Distributed enabled. Start with the defaults and adjust individual values only to address an observed performance issue.

### Distributed workers

All fields below use the prefix `Execution:Distributed:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `WorkerPollingMilliseconds` | 250 | Polling interval for pending work in milliseconds. Lower values reduce claiming latency but increase database queries. |
| `MaxConcurrentExecutions` | 4 | Maximum concurrent executions per execution Server, not a cluster-wide total. |
| `RecoveryProbeSeconds` | 30 | Seconds of inactivity before recovery of a Running record may be probed. The distributed lock still decides ownership; this is not a task timeout. |
| `LockAcquireTimeoutMilliseconds` | 500 | Maximum wait to acquire an execution lock, in milliseconds. |

All of these fields must be positive integers and are used for worker coordination in Distributed mode.

### Execution events and replay

All fields below use the prefix `Execution:Distributed:EventStream:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `Provider` | Postgres | `Postgres`: store and replay events in PostgreSQL without Redis. `Redis`: store and replay events through Redis Streams. Redis does not replace the PostgreSQL database, task records, or locks. |
| `ReadPollingMilliseconds` | 250 | Delay between reads when no new events exist, in milliseconds; must be positive. |
| `ReadBatchSize` | 100 | Maximum events per read; must be positive. |
| `WriteIntervalMilliseconds` | 250 | Batch-write delay measured from the first pending event, in milliseconds. `0` writes immediately; negative values are invalid. |
| `WriteBatchSize` | 100 | Event-count threshold for a write batch; must be positive. |
| `Redis:ConnectionString` | Empty | Required when Redis is selected. Related Servers must use the same Redis service, for example `redis:6379,password=...`. |
| `Redis:StreamTtlMinutes` | 1440 | Redis Stream retention in minutes, defaulting to 24 hours; must be positive when Redis is selected. Expired events can no longer be replayed from that Stream. |



## Common settings

These settings apply to either topology. In split deployments, configure them according to each Server’s role.

### Server endpoints and directories

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `ASPNETCORE_URLS / --urls` | Local default port 30816; container runtime supplies its binding | Listening URLs. Use `--urls http://127.0.0.1:30816` for local access or bind another address as required. Separate multiple URLs with semicolons. |
| `ASPNETCORE_ENVIRONMENT` | Production | Selects environment-specific JSON, such as `appsettings.Production.json`. Common names are Development, Staging, and Production; custom names are allowed. |
| `AgwDataDir` | ~/agw | AGW data root for runtime data, Skills, encryption keys, and related files. Supports `~`; other relative paths resolve from the process working directory. |
| `AgwLogDir` | ./logs | Separate log directory; moving the data root does not move it. Supports `~`, with other relative paths relative to the working directory. |
| `AllowedHosts` | * | HTTP Host filtering. `*` allows any hostname; use semicolon-separated hostnames such as `agw.example.com;localhost` to restrict it. This is not the client-origin list. |



### Database

All fields below use the prefix `Database:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `Provider` | sqlite | `sqlite`: a local SQLite file for standalone use. `postgres`: a PostgreSQL service supporting split and distributed deployment. These are the two supported values. |
| `ConnectionString` | Data Source=agw.db | Connection string for the selected database. SQLite uses `Data Source=...`; PostgreSQL uses `Host=...;Port=5432;Database=...;Username=...;Password=...` with a nonempty Host. Change it together with Provider. |



### Setup, origins, and reverse proxies

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `Setup:AdminPassword` | Unset | Initial administrator password, 8–256 characters. Inject through the environment or Secrets for unattended setup; it does not overwrite existing authentication. Initialize on Control Plane in a split deployment. |
| `Auth:AllowedOrigins` | agw://app, http://localhost:3000, http://127.0.0.1:3000 | Allowed client Origin array for CORS and origin checks. Match the actual client scheme and port. The array is empty if omitted. |
| `ReverseProxy:TrustedProxies` | Template contains 127.0.0.1, 172.16.0.0/12, 10.0.0.0/8 | Trusted proxies. The current implementation adds only individual IP addresses; CIDR entries are not applied. Configure actual proxy IPs. It processes forwarded For, Host, and Proto headers with a forward limit of 1. |

Configure arrays with numeric indices, such as `Auth__AllowedOrigins__0=agw://app`. Overrides merge by index; overriding index 0 does not remove template entries 1 and 2. Check the complete resulting list.

### Integration OAuth URLs

All fields below use the prefix `Integrations:OAuth:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `PublicBaseUrl` | http://localhost:30816 | Public Server base URL used to construct the OAuth callback at `api/integrations/oauth/callback`. For remote deployments, use the public URL reachable by the browser. |
| `WebBaseUrl` | http://localhost:3001 | Web base URL used after OAuth completes. Set the actual Web URL when using bundled Web or a reverse proxy. |

Both accept absolute HTTP(S) base URLs without user information, query strings, or fragments. Omitted or blank values fall back to the current request base URL.

### Conversation history writes

All fields below use the prefix `ConversationHistory:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `Mode` | Interval | `Immediate`: write pending data immediately. `Interval`: buffer and flush periodically. `TurnEnd`: primarily flush when a turn ends. Reaching the buffer limit also triggers a flush. |
| `FlushIntervalSeconds` | Template: 10; omitted: 5 | Flush interval in seconds for Interval mode; must be positive and within the supported timer range. |
| `MaxBufferedBytes` | 16777216 (16 MiB) | Buffer limit in bytes; must be positive. Reaching it triggers an early flush. |

These settings control persistence timing, not whether live output is visible. Buffering reduces writes, but abnormal termination can lose unflushed data. Choose Immediate when prompt persistence matters.

### Shell tool

All fields below use the prefix `Agents:Shell:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `Backend` | local | `local`: run commands in the project workspace on the execution host. `docker`: use the Docker Shell executor, mounting the primary workspace at `/workspace` and additional directories at `/project-directories/{id}`. It requires Docker; networking is currently disabled and timeout is 30 seconds. |

This selects only the AGW Shell tool backend. It does not change Server deployment mode or install external Agent CLIs.

### OpenTelemetry

All fields below use the prefix `OpenTelemetry:`.

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `ServiceName` | Template: Agw | Telemetry service name. Split Hosts replace the template value Agw with `Agw.ControlPlane` or `Agw.DataPlane`; when omitted, the fallback is `Agw.{HostProfile}`. |
| `ServiceVersion` | 1.0.0 | Service-version label in telemetry. |
| `OtlpEndpoint` | Template: empty; effective fallback: http://localhost:4317 | OTLP receiver URL. Empty or omitted values still use the fallback; they do not disable export. |



### Logging configuration

| Setting | Default | Purpose and accepted values |
| --- | --- | --- |
| `Logging:LogLevel:Default` | Information | Default Microsoft logging level; override a category with `Logging:LogLevel:{category}`. |
| `Logging:LogLevel:Microsoft.AspNetCore` | Warning | ASP.NET Core category level. |
| `Logging:LogLevel:Microsoft.EntityFrameworkCore` | Warning | EF Core category level. |
| `Serilog:Using` | Console, File, Async sinks | Assemblies providing Serilog configuration extensions. |
| `Serilog:MinimumLevel:Default` | Debug | Default minimum level for the current Serilog pipeline. |
| `Serilog:MinimumLevel:Override:Microsoft.AspNetCore` | Warning | Override the ASP.NET Core category. |
| `Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore` | Warning | Override the EF Core category. |
| `Serilog:MinimumLevel:Override:System` | Warning | Override System; additional categories use the same structure. |
| `Serilog:WriteTo:0:Name` | Async | Name of the template’s asynchronous sink wrapper. |
| `Serilog:WriteTo:0:Args:configure:0:Name` | Console | Console sink inside the asynchronous wrapper. |
| `Serilog:WriteTo:0:Args:configure:0:Args:outputTemplate` | See below | Console format containing timestamp, level, source, TraceId, SpanId, thread, message, and exception. |
| `Serilog:Enrich` | FromLogContext, WithMachineName, WithThreadId, WithOpenTelemetryTraceId, WithOpenTelemetrySpanId | Add context, machine, thread, and tracing identifiers. |

The Host reads Serilog separately from `appsettings.json` and `appsettings.{ASPNETCORE_ENVIRONMENT}.json`. Do not assume `Serilog__...` environment variables override this pipeline. Edit the relevant JSON and restart to change its output or levels. `Logging` and `Serilog` are separate level configurations; the main output currently uses Serilog.

All Microsoft logging levels are `Trace` (finest tracing), `Debug` (diagnostics), `Information` (normal activity), `Warning` (potential trouble), `Error` (failed operations), `Critical` (severe failures), and `None` (disabled). Serilog supports `Verbose`, `Debug`, `Information`, `Warning`, `Error`, and `Fatal`. Verbose is its finest tracing level and Fatal denotes severe failures; its minimum levels do not include None.

WriteTo Name, Using, and Enrich values are plugin names, not fixed enums. The table lists the current template. The Host additionally writes `AgwLogDir/application-{profile}-.log`, rolling hourly, retaining 30 files, and flushing every second. These values are fixed in code rather than configurable keys.

```text
[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u3}] [{SourceContext}] [TraceId:{TraceId}] [SpanId:{SpanId}] [ThreadId:{ThreadId}] {Message:lj}{NewLine}{Exception}
```

### Authentication and API Keys

Remote Web signs in with the administrator password and receives a Cookie. Desktop, Mobile, and automation use API Keys, sent as Bearer credentials:

```http
Authorization: Bearer agw_<your-token>
```

API Key plaintext is returned only on creation. Store and supply it through the environment or Secrets, and revoke unused keys. Authentication uses the key creator’s stable ID. Multiple login accounts come from the third-party sign-in configuration below; there are currently no configuration keys for roles, API Key scopes, or JWT.

Administrator password hashes, initialization state, and session versions are stored in the database’s global `auth` group in `setting`; API Key hashes are in `api_token`. Management features maintain these values; they are not appsettings entries. Password changes update the session version. Hosts refresh every second and discard cached credentials if refresh fails. To recover a forgotten password, stop Server and run `agw-server auth reset-password`.

### Third-party sign-in

Third-party sign-in lets people reach Web and Desktop with an organization account. The first sign-in with an account creates an isolated local user numbered from `10000`; the administrator remains `1001`. With no provider configured, the administrator password and API Keys continue to work. See [Third-party account sign-in]({{< relref "/docs/features/oidc-login" >}}) for the resulting behavior.

Register AGW at the provider as a web application (confidential client). The callback URL is built from the provider ID. Desktop users go through the same URL: the provider returns the browser to Server, not to the desktop application:

```text
https://agw.example.com/api/auth/oidc/callback/company
```

The following keys are all prefixed with `Auth:Oidc:`, where `{id}` is the ID you choose for a provider.

| Setting | Default | Purpose and values |
| --- | --- | --- |
| `PublicBaseUrl` | Empty | The browser-visible Server origin the provider returns to. `api/auth/oidc/callback/{id}` is appended to it. Use a full origin without a path: HTTPS in production, loopback HTTP allowed in development. Required once any provider is enabled. |
| `WebBaseUrl` | Empty | Where the browser lands after sign-in. Leave empty when Web shares the Server origin; set it during source development, where Web runs on `3001` and the backend on `30816`. |
| `Providers:{id}:Enabled` | false | Whether this provider is available. |
| `Providers:{id}:Type` | Oidc | `Oidc` discovers endpoints from Authority and requests `openid profile email`. `OAuth2` uses explicit endpoints and claim names. |
| `Providers:{id}:DisplayName` | Provider ID | The name shown on the sign-in button. |
| `Providers:{id}:ClientId` | Empty | Client ID issued when registering AGW at the provider. Required. |
| `Providers:{id}:ClientSecret` | Empty | Matching client secret. Required; inject it through the environment or Secrets. |
| `Providers:{id}:Authority` | Empty | Required for `Oidc`, such as `https://sso.example.com/realms/company`. |
| `Providers:{id}:AuthorizationEndpoint` | Empty | Required for `OAuth2`: where the user authorizes AGW. |
| `Providers:{id}:TokenEndpoint` | Empty | Required for `OAuth2`: where Server exchanges the authorization code. |
| `Providers:{id}:Issuer` | Empty | Required for `OAuth2`: identifies the account source and, with the account ID, determines the user. |
| `Providers:{id}:IdentitySource` | UserInfo | `UserInfo` reads the account from the user information endpoint. `AccessToken` reads it from a signed JWT access token. |
| `Providers:{id}:UserInfoEndpoint` | Empty | Required when `IdentitySource` is `UserInfo`. |
| `Providers:{id}:AccessTokenIssuer` | Empty | Required when `IdentitySource` is `AccessToken`: validates who issued the token. |
| `Providers:{id}:AccessTokenAudience` | Empty | Required when `IdentitySource` is `AccessToken`: validates the intended recipient. |
| `Providers:{id}:AccessTokenJwksUri` | Empty | Required when `IdentitySource` is `AccessToken`: where signing keys are published. |
| `Providers:{id}:ClientAuthMethod` | Post | How OAuth2 client credentials are sent: `Post` in the request body, `Basic` in the header. |
| `Providers:{id}:UsePkce` | false | Enables S256 for OAuth2. `Oidc` always uses it. |
| `Providers:{id}:Scopes` | Empty | OAuth2 scopes, configured by index, such as `Scopes__0=read:user`. |
| `Providers:{id}:SubjectClaim` | sub | Field holding the account ID; GitHub uses `id`. |
| `Providers:{id}:DisplayNameClaim` | name | Field holding the display name; GitHub uses `login`. |
| `Providers:{id}:EmailClaim` | email | Field holding the email address. A missing display name or email does not block sign-in. |

Provider IDs use lowercase letters, digits, and hyphens, up to 64 characters, such as `company` or `entra-id`. Each ID owns its callback URL; keep it stable after registration.

Example configuration without secrets:

```json
{
  "Auth": {
    "Oidc": {
      "PublicBaseUrl": "https://agw.example.com",
      "Providers": {
        "company": {
          "Enabled": true,
          "Type": "Oidc",
          "DisplayName": "Company account",
          "Authority": "https://sso.example.com/realms/company",
          "ClientId": "agw"
        }
      }
    }
  }
}
```

Inject the matching secret as `Auth__Oidc__Providers__company__ClientSecret`. Keep it out of appsettings, frontend environment files, and screenshots. Services that offer OAuth2 only, such as GitHub, use explicit endpoints:

```json
{
  "Auth": {
    "Oidc": {
      "Providers": {
        "github": {
          "Enabled": true,
          "Type": "OAuth2",
          "DisplayName": "GitHub",
          "AuthorizationEndpoint": "https://github.com/login/oauth/authorize",
          "TokenEndpoint": "https://github.com/login/oauth/access_token",
          "UserInfoEndpoint": "https://api.github.com/user",
          "Issuer": "https://github.com/login/oauth",
          "IdentitySource": "UserInfo",
          "UsePkce": true,
          "ClientAuthMethod": "Post",
          "Scopes": ["read:user"],
          "SubjectClaim": "id",
          "DisplayNameClaim": "login",
          "EmailClaim": "email",
          "ClientId": "github-client-id"
        }
      }
    }
  }
}
```

Authority values for common platforms:

| Platform | Authority |
| --- | --- |
| Google | `https://accounts.google.com` |
| Microsoft Entra ID | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| Keycloak | `https://sso.example.com/realms/<realm>` |
| Authentik | `https://sso.example.com/application/o/<application-slug>/` |

Restart the relevant Server after changing these values. In a split deployment, route sign-in requests to Control Plane; Data Plane runs executions with the resulting local credentials. All replicas share one database and Data Protection keys, so a Desktop one-time code can be exchanged on a different replica. Disabling a provider blocks new sign-ins and pending Desktop exchanges; Cookies and API Keys already issued are revoked separately.

## Example and verification

This example illustrates configuration syntax. Supply real connection values through the environment or Secrets:

```bash
export Database__Provider=postgres
export Database__ConnectionString='Host=db;Port=5432;Database=agw;Username=agw;Password=REPLACE_ME'
export Execution__Provider=Distributed
export DistributedLock__Provider=postgres
agw-server --urls http://127.0.0.1:30816
```

For split deployment, supply matching database and execution settings to each Host, initialize Control Plane first, then start Data Plane. The example’s `agw-server` is Standalone; split deployment uses the corresponding Host executables.

After restarting, inspect startup logs, the listening URL, database connectivity, and client sign-in. Validate execution tuning with a small task while observing latency and load. For startup errors, check enum names, numeric ranges, connection strings, and Distributed dependencies. Do not expose passwords or API Keys when sharing logs.

## Implementation and references

- [Host template](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Host/appsettings.json)
- [Host configuration readers](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Host/Program.cs)
- [Deployment defaults](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Host/Hosting/ServerDeploymentConfiguration.cs)
- [Execution settings](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/Configuration/ExecutionRuntimeOptions.cs)
- [History settings](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Projects/Infrastructure/ConversationHistoryOptions.cs)
- [OAuth URLs](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/Application/OAuth/OAuthRedirectUriResolver.cs)
- [Shell backend](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools/Impl/ContextualTools/Shell/ShellContextualTool.cs)
- [Authentication](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Auth/README.md)
