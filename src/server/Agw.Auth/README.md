# Agw.Auth

`Agw.Auth` owns the Server administrator authentication and authorization interface.

## Authentication modes

- Remote Web requests sign in with the administrator password and receive the `agw.session` Cookie.
- Desktop, Mobile, and automation clients send named `Authorization: Bearer agw_...` Tokens. The resulting principal uses the Token creator's user ID.
- Direct loopback requests with a localhost Host and no forwarding headers use the `LocalTrusted` identity.

Cookie and `LocalTrusted` requests use the built-in administrator ID. Bearer requests preserve the creating user's ID while using a Token-specific display name, so downstream ownership must read the `NameIdentifier` claim rather than `Identity.Name`. Agw does not currently provide multiple login accounts, roles, Token scopes, or JWT authentication.

## Runtime

`AddAuth()` registers Cookie authentication, antiforgery, authorization, password hashing, authentication-attempt limiting, and the scoped current-user service. `UseAgwAuth()` preserves the required runtime order:

1. ASP.NET Core Cookie authentication.
2. Bearer Token or `LocalTrusted` principal creation.
3. Copy the authenticated `ClaimsPrincipal` into the `UserInfoUtil` logical thread-local for downstream code, then restore it when the request ends.
4. Antiforgery validation for unsafe Cookie and `LocalTrusted` API requests.
5. Authentication protection for `/api` and `/a2a` paths.

The Host must call `UseAuthorization()` after `UseRouting()` so endpoint authorization metadata remains effective. A2A and the execution SignalR Hub additionally use `RequireAuthorization()`.

`UserInfoUtil.UserId` is nullable. Call `RequiredUserId` when authentication is mandatory: it throws `AuthenticationRequired` for anonymous flows and returns `"1001"` when an authenticated principal has no usable user ID.

## State seam

`IAuthenticationStateStore` exposes administrator password-hash and Web-session-version state. `IApiTokenStore` separately owns named Token listing, creation, validation, and revocation.

`Agw.Setup.JsonInitializationStateStore` remains the production Adapter for password and session state. `Agw.Infrastructure.Auth.EfApiTokenStore` stores Token hashes in the `api_token` database table. Each row also records `create_by` and UTC `create_time` through the standard entity-audit interceptor. Successful validation returns that creator ID for execution ownership, task-session bindings, checkpoints, User Memory, and later audit writes. Token plaintext is returned only once at creation and is never persisted.

On startup, `LegacyApiTokenMigrator` imports any hashed Token records from an older `server-state.json`. Old records did not include a creator, so they are attributed to the built-in administrator while retaining their original creation time. The JSON `tokens` property is removed only after the database write succeeds; retrying after an interrupted write is idempotent.

## Compatibility

The public routes remain under `/api/auth`. Cookie names, Token format, CSRF header, anonymous paths, error codes, and response envelopes are compatibility-sensitive and must not change without coordinated client updates.
