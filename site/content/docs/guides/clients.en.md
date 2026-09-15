---
title: "Web, Desktop, and Mobile"
description: "Choose a client, connect to the right Server, and manage conversations."
weight: 110
lastmod: 2026-09-15
translationKey: docs/guides/clients
---

Prerequisite: an initialized Server. Clients do not replace the Server-side model, file, or execution environment.

| Client | Connection | Use case |
| --- | --- | --- |
| Web | Remote administrator Cookie; same-origin APIs | Browser management and chat |
| Desktop | Named Bearer Token; multiple Server profiles | Local or remote daily workspace |
| Mobile | Named Bearer Token | Conversations and project access on mobile |

## Connect

1. For Web, open the Server URL, or port `3001` during source development.
2. Desktop Full can use its bundled Server. Configure an existing Server URL and Token in Client.
3. Configure Mobile with a Server URL reachable from the device and a Token. Device localhost usually is not your development computer.
4. Start a short conversation and verify the target Server, Project, and history.

Desktop centers on Chat; Projects and other administration routes live in Settings. Each Server profile uses an isolated cache. Changing its URL or Token discards the old connection’s cache so data from different Servers does not get mixed.

![AGW Desktop chat: select a Project and Agent at the top, then enter a message below.](/images/screenshots/desktop-chat.png)
{caption="AGW Desktop chat: select a Project and Agent at the top, then enter a message below."}

## Runtime differences

Desktop renderer uses its own port `3000` and does not require the Web development server. Full's Server daemon continues after the desktop window closes; closing minimizes to the tray by default.

Mobile uses Expo with native projects generated through CNG. These docs provide the [source-development path]({{< relref "/docs/development/setup" >}}), without assuming an app-store package exists. Use HTTPS for remote access and ensure the proxy supports execution WebSockets.

## Implementation and references

- [Desktop](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [Mobile](https://github.com/zxyao145/agw/blob/main/src/clients/mobile/README.md)
