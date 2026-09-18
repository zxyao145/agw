# Agw.Auth

Agw.Auth owns administrator, OIDC and OAuth2 authentication, local user identities, browser sessions, Desktop login handoff, named API Tokens, CSRF validation, and authorization guards.

## Authentication modes

- Web signs in with the administrator password or a configured OIDC/OAuth2 provider and receives the `agw.session` Cookie.
- Desktop can sign in through the system browser or use a manually configured `Authorization: Bearer agw_...` Token. Mobile and automation retain the named Token flow.
- Direct loopback requests with a localhost Host and no forwarding headers can use `LocalTrusted`. Explicit invalid credentials, rejected OIDC sessions, and the Desktop explicit-auth marker must never fall back to the administrator.

Administrator `1001` remains unchanged. New OIDC user IDs start at `10000` and are represented as decimal strings in claims and owner contracts. External identities use verified `(issuer, sub)`; email is display information and never merges accounts or grants administrator access. Ordinary users own their resources and can manage their own Tokens through a Web session.

## The two authentication exchanges

1. **Server → IdP:** the OIDC or OAuth2 handler exchanges the authorization code using server-side client credentials. OIDC validates the ID Token; OAuth2 uses configured UserInfo or a signed JWT access token. OAuth2 can enable its own S256 PKCE proof.
2. **Desktop → Server:** after successful authentication, Server returns a two-minute handoff code through `agw-desktop://auth/complete`. Desktop submits that code and its independently generated verifier over HTTPS; Server then creates and returns an Agw Token.

The Desktop verifier is not the Server's OIDC verifier. Desktop never receives the IdP client secret or upstream Token. Web signs in directly with a local Cookie and does not use the Desktop exchange.

The `auth_desktop_login_grant` table is an application handoff mechanism, not an OIDC requirement. It stores only code hashes, user identity, Desktop challenge, session version, and expiry. Grant consumption and Token creation share one database transaction, including across replicas. Successful exchange deletes the grant; a hosted task deletes expired grants in batches.

## Registration and pipeline

`AddAuth()` registers local Cookie authentication, antiforgery, password hashing, authentication-attempt limiting, and the scoped current-user service. `AddOidcAuthentication(configuration, environment)` registers the configured OIDC/OAuth2 schemes and grant cleanup only on Control Plane and Standalone. Data Plane validates local Cookie/Bearer credentials without exposing login callbacks.

`UseAgwAuth()` maintains this order:

1. Establish the WebSocket feature and check allowed Origins.
2. Authenticate the Cookie or handle the OIDC protocol callback.
3. Validate Bearer credentials or, when eligible, create the LocalTrusted principal.
4. Establish `UserInfoUtil.Current` for application code and restore it after the request.
5. Validate CSRF on unsafe Cookie/LocalTrusted API requests.
6. Guard protected `/api` and `/a2a` routes.

The Host calls `UseAuthorization()` after routing. A2A and the execution Hub also require authentication.

Default OIDC claim actions remove `iss` before `OnTicketReceived`. The OIDC handler captures the validated issuer in `OnTokenValidated`; the OAuth2 handler obtains a verified issuer and subject from UserInfo or JWT validation. OAuth2 callbacks start timing at the remote-handler entry and reject non-GET or uninitialized callbacks before code exchange. Neither handler provisions in the token-validation callback: all protocol checks must finish before resolving or creating a local user.

## Persistence and ownership

Auth owns:

| Table | Purpose |
| --- | --- |
| `auth_user` | Local user profile and OIDC session version |
| `auth_external_identity` | Unique issuer/subject mapping; one identity per user in this version |
| `auth_user_id_sequence` | Transactional ID allocation starting at 10000 |
| `auth_desktop_login_grant` | One-time proof-bound Desktop handoff |
| `api_token` | Hashed named API Tokens |

`IAuthDbContext` exposes Auth-owned entities. The Infrastructure identity adapter has narrowly scoped authentication-boundary access for verified identities, signed local user IDs, grant proofs, and expiry cleanup. It must not expose general cross-user queries. Normal entity reads remain fail-closed without an owner.

Infrastructure coordinates registration with `IUserProjectInitializer`. It allocates a local ID, establishes the user context, inserts the profile and identity, and creates owner-specific `default-built-in` and `a2a` projects in one transaction. This requires the Auth and Projects persistence seams to resolve to the same scoped `AgwDbContext`; splitting contexts requires a new transaction design. A rollback test fails after saving real default projects and verifies no user, identity or project remains. Default workspaces are separate by project ID; administrator credentials and business records are not copied.

The global `auth` group in the Settings-owned `setting` table still holds the administrator password and administrator session version through `IServerAuthStatePersistence`. OIDC Cookies use the local user's session version instead. Changing the administrator password does not invalidate ordinary OIDC user sessions.

## Configuration and endpoints

Configure `Auth:Oidc:PublicBaseUrl` and `Auth:Oidc:Providers:<id>`. OIDC providers require Authority; OAuth2 providers require explicit authorization and Token endpoints, an Issuer, ClientId and ClientSecret. Secrets come from environment variables or Secrets. Changes require restart. Production requires HTTPS. Development alone permits HTTP loopback.

See [OIDC/OAuth2 deployment instructions](../../../docs/4.Deployment.md#oidc-login), the [OIDC design](../../../docs/approachs/2026-09-17-oidc.md) and the [OAuth2 design](../../../docs/approachs/2026-09-18-oauth2.md).

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/api/auth/oidc/providers` | Public enabled-provider IDs, display names and types |
| GET | `/api/auth/oidc/login` | Explicit browser challenge |
| GET | `/api/auth/oidc/callback/{providerId}` | Handler-owned protocol callback |
| POST | `/api/auth/desktop/exchange` | One-time handoff code and Desktop verifier |
| POST | `/api/auth/desktop/logout` | Revoke the actually presented Bearer Token |
| GET | `/api/auth/session` | Local session and user information |

JSON endpoints use Bens.Results. Callback routes retain protocol redirects. Existing password, Cookie logout, and named Token management endpoints remain available.

Cookies are HttpOnly, Lax, and secure in HTTPS deployments, with the existing twelve-hour sliding lifetime. Unsafe Cookie requests retain CSRF checks. The two Desktop endpoints authenticate through their own proof or actual Bearer header and do not authorize using an ambient Cookie.

Desktop exchanges are single-use: an expired, invalid or consumed grant returns `401_0005`. A lost successful response requires a new login. Desktop persists the Token through OS-backed encryption, clears its local credential on logout, and reports any unconfirmed remote revocation.

## Observability and compatibility

The `Agw.Auth` meter reports OIDC/OAuth2 login outcomes, callback duration, OAuth2 Token/UserInfo/JWT validation, PKCE, exchange outcomes/duration, and cleanup failures. Logs use bounded provider/client/result fields and local user IDs after authentication. Do not log raw callbacks, either verifier, authorization/handoff codes, credentials, upstream responses or Tokens. The Host keeps framework OIDC/OAuth2 protocol errors at `Error` for diagnosis and uses the module's sanitized events for bounded failure details; verbose protocol logging remains disabled. Failure events record stage, category, exception type and TraceId without exception messages or payloads; provisioning and Desktop grant failures are distinct from IdP availability failures.

No authentication state file, legacy JSON Token import, or `X-API-Key` fallback is introduced. Session responses retain API major version 1 and add displayName, loginProvider and isAdmin. Provider failure affects new login; existing local sessions follow their local lifetime or Token revocation.
