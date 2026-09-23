---
title: "Install and configure Server"
description: "Choose an installation, initialize Server, and connect clients."
weight: 20
lastmod: 2026-09-23
translationKey: docs/start/install
aliases: ["/en/docs/start/setup/"]
---

AGW needs a running Server and a client to operate it. For a first local installation, choose Desktop Full. If Server already exists, use Desktop Client or a browser. After installation, configure a model service or external agent to start a conversation.

## Installation paths

| Method | Best for | Key differences |
| --- | --- | --- |
| Desktop Full | A local graphical workspace | Installs both the desktop client and Server; Server runs as a current-user background service |
| Desktop Client | Connecting to an existing Server | Installs only the desktop client, without Server; connects to a local or remote Server |
| Docker | Containerized self-hosting | Runs Server in a container with Web included; mounts provide persistent data and workspace access |
| Portable Server | Hosting directly on a machine | Runs the Server executable directly with Web included; no Docker or desktop client required |
| Source | Development and debugging | Build and run the backend and required clients yourself to modify code and debug modules |

## Desktop

1. Open [GitHub Releases](https://github.com/zxyao145/agw/releases).
2. Select Full or Client for your platform. Windows and Ubuntu currently support x64; macOS supports x64 and arm64.
3. Complete initialization on the first Full launch, or connect Client to an existing Server.

Full and Client share an application identity and are mutually exclusive variants. Full installs a current-user Server daemon; closing Desktop does not stop it. Packages are currently unsigned and not notarized.

![AGW Desktop chat: select a Project and Agent at the top, then enter a message below.](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop chat: select a Project and Agent at the top, then enter a message below."}

## Docker

Choose a deployment approach for your needs:

- [Standalone and Docker deployment]({{< relref "/docs/operations/standalone" >}}): run the complete service in one Server for local trials or single-server self-hosting.
- [Split Control/Data Plane deployment]({{< relref "/docs/operations/split" >}}): run management and scheduling separately from task execution when you need independent deployment or more execution nodes. The guide covers the shared database, directories, and Nginx routing.

Configure a domain, HTTPS, and a reverse proxy for shared or remote access.

The Docker image does not include any external agents, such as Claude Code, Codex, or Pi. Install and configure them in the container yourself if needed.

## Source

Follow [Development setup]({{< relref "/docs/development/setup" >}}), starting the Standalone Host before Web. The backend defaults to port `30816`; Web development uses `3001`.

## Configure Server

Prerequisites: Server is running, database configuration is valid, and its data directory is writable. Standalone defaults to SQLite with InProcess execution.

### First-run initialization

1. Locally open `http://localhost:30816/setup`. Docker and Portable Server include Web; source users can initialize the server before starting Web.
2. Set the administrator password. Domain or forwarded access also requires the one-time Setup Code from the startup log; direct local access does not.
3. Submit and wait for database initialization. The application opens without another restart.

After initialization, authentication settings are saved in the database, so later starts do not repeat setup. Preserve the database and encryption keys when moving or backing up the service; see [Backup and upgrades]({{< relref "/docs/operations/backup" >}}).

![The Server’s first-run setup page: set an administrator password when accessing locally. Initialization was not submitted for this screenshot.](/images/screenshots/server-setup.png)
{width="480" caption="The Server’s first-run setup page: set an administrator password when accessing locally. Initialization was not submitted for this screenshot."}

### Connect clients

- Remote Web signs in with the administrator password and receives a session Cookie. With identity providers configured, the sign-in page also shows third-party account buttons, and Desktop can obtain its API Key through them; see [Configuration and authentication]({{< relref "/docs/operations/configuration" >}}).
- Desktop, Mobile, and automation use API Keys, sent in the `Authorization: Bearer agw_...` header. Plaintext is shown only once when a key is created.
- Desktop Full uses the Server-owned setup page, then provisions its own API Key and protects it with the operating system credential store.

An API Key is the client’s access key to Server. To connect Desktop Client or Mobile remotely, create a named API Key in the signed-in management interface, then enter the Server URL and full key in the client. On a phone, `localhost` refers to the phone, not your computer.

### Unattended initialization

Inject the initial password through `Setup__AdminPassword`. In split deployments, initialize Control Plane only; Data Plane has no Setup page. Keep real passwords out of code and documentation.

Setup parameters cannot overwrite an initialized server. Select the database and execution mode through standard configuration before startup, not through the Setup form.

Continue with [Your first conversation]({{< relref "/docs/start/first-chat" >}}). If login fails, first verify that the client connects to the intended Server.

## Implementation and references


- [Desktop packages](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [Server deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
- [Setup](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Setup/README.md)
- [Authentication](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Auth/README.md)
