# Agw.Auth

`Agw.Auth` owns the Server administrator authentication and authorization interface.

## Authentication modes

- Remote Web requests sign in with the administrator password and receive the `agw.session` Cookie.
- Desktop, Mobile, and automation clients send named `Authorization: Bearer agw_...` Tokens. The resulting principal uses the Token creator's user ID.
- Direct loopback requests with a localhost Host and no forwarding headers use the `LocalTrusted` identity.

Cookie and `LocalTrusted` requests use the built-in administrator ID. Bearer requests preserve the creating user's ID while using a Token-specific display name, so downstream ownership must read the `NameIdentifier` claim rather than `Identity.Name`. Agw does not currently provide multiple login accounts, roles, Token scopes, or JWT authentication.

## Runtime

`AddAuth()` registers Cookie authentication, antiforgery, authorization, password hashing, authentication-attempt limiting, and the scoped current-user service. `UseAgwAuth()` reads the `Auth:AllowedOrigins` array from Host configuration and preserves the required runtime order:

1. ASP.NET Core WebSocket middleware establishes the WebSocket feature.
2. Agw WebSocket Origin middleware allows same-origin requests and configured cross-origin requests.
3. ASP.NET Core Cookie authentication.
4. Bearer Token or `LocalTrusted` principal creation.
5. Copy the authenticated `ClaimsPrincipal` into the `UserInfoUtil` logical thread-local for downstream code, then restore it when the request ends.
6. Antiforgery validation for unsafe Cookie and `LocalTrusted` API requests.
7. Authentication protection for `/api` and `/a2a` paths.

The Host must call `UseAuthorization()` after `UseRouting()` so endpoint authorization metadata remains effective. A2A and the execution SignalR Hub additionally use `RequireAuthorization()`.

`UserInfoUtil.UserId` is nullable. Call `RequiredUserId` when authentication is mandatory: it throws `AuthenticationRequired` for anonymous flows and for authenticated principals without a usable stable user ID. There is no administrator-ID fallback.

## State seam

`IAuthenticationStateStore` exposes administrator password-hash and Web-session-version state. `IApiTokenStore` separately owns named Token listing, creation, validation, and revocation.

`Agw.Setup.DatabaseInitializationStateStore` supplies database-backed password/session snapshots through `IServerAuthStatePersistence`; the Infrastructure adapter owns the single global `auth` configuration row in `setting`. `Agw.Infrastructure.Auth.EfApiTokenStore` stores Token hashes in the `api_token` database table. Each row also records `create_by` and UTC `create_time` through the standard entity-audit interceptor. Successful validation returns that creator ID for execution ownership, task-session bindings, checkpoints, User Memory, and later audit writes. Token plaintext is returned only once at creation and is never persisted.

Legacy JSON Token import is removed. No authentication or initialization code reads or writes `server-state.json`. Password changes increment the database session version, and replicas refresh their snapshots once per second. Failed refreshes discard cached credentials.

## Compatibility

The public routes remain under `/api/auth`. Cookie names, Token format, CSRF header, anonymous paths, error codes, and response envelopes are compatibility-sensitive and must not change without coordinated client updates.
