# Agw.Setup Guidelines

These instructions supplement the repository-root `AGENTS.md` for work under `Agw.Setup`.

## Module Boundary

`Agw.Setup` owns first-run setup, initialization guards, setup-code validation, and the combined `server-state.json` persistence adapter. `Agw.Auth` owns login, Cookie and Bearer authentication, `LocalTrusted`, CSRF, token management, and authorization.

`JsonInitializationStateStore` implements both the setup state contract and the `Agw.Auth` authentication-state seam so the combined document continues to use one lock and atomic replacement. Do not introduce a second writer or split authentication state into static configuration.

## API and Security

- Setup JSON endpoints return Bens.Results envelopes and use shared `AgwException` error codes.
- Direct loopback setup may be trusted; forwarded or domain setup requires the one-time Setup Code.
- Never persist administrator password or API Token plaintext. Never return stored password/token hashes or protected credential payloads.
- Preserve the existing `server-state.json` schema unless a coordinated compatibility change is explicitly requested.

## Verification

Run commands from the repository root:

```bash
dotnet build Agw.slnx
dotnet test tests/Agw.Setup.Tests/Agw.Setup.Tests.csproj
dotnet test tests/Agw.Auth.Tests/Agw.Auth.Tests.csproj
```

Changes to the shared setup/auth state seam normally require both focused test projects.
