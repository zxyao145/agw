# Agw.Auth

`Agw.Auth` owns the Server administrator authentication and authorization interface.

## Authentication modes

- Remote Web requests sign in with the administrator password and receive the `agw.session` Cookie.
- Desktop, Mobile, and automation clients send named `Authorization: Bearer agw_...` Tokens.
- Direct loopback requests with a localhost Host and no forwarding headers use the `LocalTrusted` identity.

All authenticated modes create the same administrator principal. Agw does not currently provide multiple users, roles, Token scopes, or JWT authentication.

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

`IAuthenticationStateStore` is the module's persistence seam. It exposes password hash and session version snapshots plus named Token creation, validation, and revocation.

`Agw.Setup.JsonInitializationStateStore` is the production Adapter. Authentication state remains in the existing `server-state.json` document so upgrades require no data migration and all writes continue through one lock and atomic file replacement. Token plaintext is returned only at creation; the file stores only Token hashes and metadata.

## Compatibility

The public routes remain under `/api/auth`. Cookie names, Token format, CSRF header, anonymous paths, error codes, and response envelopes are compatibility-sensitive and must not change without coordinated client updates.
