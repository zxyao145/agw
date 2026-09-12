# Agw.Setup Guidelines

These instructions supplement the repository-root `AGENTS.md` for work under `Agw.Setup`.

## Module Boundary

`Agw.Setup` owns first-run setup, initialization guards, setup-code validation, and database-backed initialization snapshots. `Agw.Auth` owns login, Cookie and Bearer authentication, LocalTrusted, CSRF, token management, and authorization.

`DatabaseInitializationStateStore` implements setup and authentication state contracts. `IServerAuthStatePersistence` is the Auth contract; its Infrastructure adapter stores the deployment-wide administrator record in the global `auth` group of `setting`. API Tokens remain in `api_token`. Never write an authentication state file or move mutable authentication state into static configuration.

## API and Security

- Setup JSON endpoints return Bens.Results envelopes and use shared `AgwException` error codes.
- Direct loopback setup may be trusted; forwarded or domain setup requires the one-time Setup Code.
- Never persist administrator password or API Token plaintext. Never return stored password/token hashes or protected credential payloads.
- Persist initialization only after database migration and seeding succeed. Concurrent setup must not overwrite an existing administrator. Increment SessionVersion when updating a password. All Hosts read the shared database; refresh failure discards cached credentials. No legacy JSON Token import or deployment configuration fallback remains.

## Verification

Run commands from the repository root:

```bash
dotnet build Agw.slnx
dotnet test tests/Agw.Setup.Tests/Agw.Setup.Tests.csproj
dotnet test tests/Agw.Auth.Tests/Agw.Auth.Tests.csproj
```

Changes to the shared setup/auth state seam normally require both focused test projects.
