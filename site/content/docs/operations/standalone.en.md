---
title: "Standalone and Docker"
description: "Run a Standalone Server with its bundled Web UI."
weight: 10
lastmod: 2026-09-25
translationKey: docs/operations/standalone
---

Standalone provides management pages, conversations, and scheduled jobs in one Server. It suits a local trial or a single host. Defaults use SQLite for storage and run tasks in the Server process, without a separate database service.

Use the Docker image, or build a Portable Server for your operating system and processor. Both include Web. Start with local access, then configure directory mounts and remote access as needed.

## Local Docker trial

```bash
docker run -d --name agw \
  --restart unless-stopped \
  -p 127.0.0.1:30816:8080 \
  -v agw-data:/data \
  ghcr.io/zxyao145/agw:latest
```

Open `http://localhost:30816/setup` and initialize. Through a Docker port mapping, the container does not see a loopback source address, so Setup asks for the one-time Setup Code; find “Agw remote setup code” in the startup log with `docker logs agw`. The image includes static Web assets, so the browser connects directly to Server. `latest` is convenient for evaluation; use a fixed release tag for a maintained deployment.

To access host projects, add an explicit bind mount and configure its container-side path as Project Workspace. Do not confuse a host path with the path visible inside the container.

## Portable Server

Portable Server is not attached to Releases. Build it from the repository root, for example:

```bash
PUBLISH_MODE=portable APP_VERSION=0.1.0 RIDS=linux-x64 ./publish.sh
```

The output lands in `artifacts/publish/portable/agw-server-<version>-<RID>/`, along with a matching archive. Start it from that directory:

```bash
./agw-server serve
```

On Windows, use `agw-server.exe serve`. The default listener is `http://127.0.0.1:30816`; override it with `ASPNETCORE_URLS` when needed. Without `ASPNETCORE_URLS`, if port 30816 is busy, Server picks a random free local port and records the actual address in `<AgwDataDir>/runtime/server.json`. Verify local Setup, Web, and a conversation before configuring remote access.

## Storage and networking

Docker uses `/data` for data. Logs default independently to `logs` under the working directory; persistent file logs need a separate mount for the configured log path. Project workspaces are also independent of the data volume.

Remote hosting requires correct AllowedHosts, trusted proxies, HTTPS, and WebSocket forwarding. The repository Compose example includes domain and proxy settings; replace them with actual environment values. See [Backup and upgrades]({{< relref "/docs/operations/backup" >}}) for the complete persistence set.

## Implementation and references

- [Compose example](https://github.com/zxyao145/agw/blob/main/deploy/compose.yaml)
- [Deployment](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
