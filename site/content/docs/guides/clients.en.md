---
title: "Web, Desktop, and Mobile"
description: "Choose a client, connect to the right Server, and manage conversations."
weight: 110
lastmod: 2026-09-23
translationKey: docs/guides/clients
---

Prerequisite: an initialized Server. Clients do not replace the Server-side model, file, or execution environment.

| Client | Connection | Use case |
| --- | --- | --- |
| Web | Session Cookie from the administrator password or a third-party account; same-origin APIs | Browser management and chat |
| Desktop | API Key, entered manually or issued by third-party sign-in; multiple Server profiles | Local or remote daily workspace |
| Mobile | API Key | Conversations and project access on mobile |

## Connect

1. For Web, open the Server URL, or port `3001` during source development.
2. Desktop Full can use its bundled Server. Configure an existing Server URL and API Key in Client.
3. Configure Mobile with a Server URL reachable from the device and an API Key. Device localhost usually is not your development computer.
4. Start a short conversation and verify the target Server, Project, and history.

Desktop centers on Chat; Projects and other administration routes live in Settings. Each Server profile uses an isolated cache. Changing its URL or API Key discards the old connection’s cache so data from different Servers does not get mixed.

![AGW Desktop chat: select a Project and Agent at the top, then enter a message below.](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop chat: select a Project and Agent at the top, then enter a message below."}

## Third-party account sign-in

When Server has identity providers enabled, the Web sign-in page shows account buttons and returns you to the page you requested. Desktop shows “Sign in with …” in the Server profile; the system browser completes authentication and Desktop receives its API Key without any manual paste. The same place offers “Sign out” to revoke that API Key. A Desktop Full profile connected to its bundled Server can also choose “Use local administrator”.

Mobile uses a manually configured API Key. Each third-party account is a separate user whose data stays apart from the administrator account. See [Configuration and authentication]({{< relref "/docs/operations/configuration" >}}) for setup and [Third-party account sign-in]({{< relref "/docs/features/oidc-login" >}}) for behavior.

## Runtime differences

Desktop renderer uses its own port `3000` and does not require the Web development server. Full's Server daemon continues after the desktop window closes; closing minimizes to the tray by default.

Mobile uses Expo with native projects generated through CNG. These docs provide the [source-development path]({{< relref "/docs/development/setup" >}}), without assuming an app-store package exists. Use HTTPS for remote access and ensure the proxy supports execution WebSockets.

## Implementation and references

- [Desktop](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [Mobile](https://github.com/zxyao145/agw/blob/main/src/clients/mobile/README.md)
