---
title: "Development setup"
description: "Install dependencies and run the backend, Web, Desktop, or Mobile independently."
weight: 10
lastmod: 2026-09-15
translationKey: docs/development/setup
---

Prerequisites: .NET 10 SDK, Node.js 24, pnpm 11.7.0, and Git. Docker Buildx is needed only for container images. These application commands run in the AGW repository; the documentation site does not depend on this toolchain.

## Backend

```bash
git clone https://github.com/zxyao145/agw.git
cd agw
git config core.hooksPath .githooks
dotnet restore Agw.slnx
dotnet tool restore
dotnet run --project src/server/Agw.Standalone.Host
```

Initialize at `http://localhost:30816/setup`. Replace `dotnet run` with `dotnet watch` for hot reload.

## Clients

Open another terminal and enter the workspace from the repository root:

```bash
cd src/clients
pnpm install
pnpm dev:web
```

Open `http://localhost:3001`. Desktop uses `pnpm dev:desktop`; its independent renderer runs on `3000` without Web. Mobile uses `pnpm dev:mobile`, or `pnpm android:mobile` / `pnpm ios:mobile`. Expo CNG generates native projects.

## What to expect after startup

Keep the backend terminal running, then start the client you need. Once Web opens, confirm Server initialization and login, then configure a model using [Your first conversation]({{< relref "/docs/start/first-chat" >}}). A loaded frontend alone does not verify its backend connection.

The backend defaults to `30816`, Web development to `3001`, and the Desktop renderer to `3000`. If a port is occupied, check for an existing development process. On a physical phone, `localhost` means the phone itself; use a computer address reachable from the phone.

## Verify

Confirm backend initialization, client connectivity, and a simple message. Physical mobile devices need a reachable backend address. External CLIs must work in the execution process environment.

The site needs only Hugo Extended and Go; see `site/README.md` for commands. Do not add it to the client Turborepo or make Web/Desktop consume its artifacts.

## Implementation and references

- [Development commands](https://github.com/zxyao145/agw/blob/main/docs/1.Development.md)
- [Client scripts](https://github.com/zxyao145/agw/blob/main/src/clients/package.json)
