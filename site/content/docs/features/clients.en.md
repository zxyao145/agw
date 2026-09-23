---
title: "Multiple clients"
description: "Access your AGW Server from a browser, desktop, or mobile device."
weight: 60
lastmod: 2026-09-23
translationKey: docs/features/clients
---

## Continue on the device that fits

AGW provides Web, Desktop, and Mobile clients. Connect to the same Server with the appropriate identity to access your authorized projects and saved conversation history, choosing the device that suits the task.

| Client | Useful for |
| --- | --- |
| Web | Management and chat in a browser without a desktop installation |
| Desktop | A daily workspace, multiple Server profiles, local or remote use |
| Mobile | Checking conversations, accessing projects, and continuing discussions on the go |

![The AGW Desktop chat workspace; Web and Mobile use interfaces adapted to their platforms.](/images/screenshots/desktop-chat.png)
{caption="The AGW Desktop chat workspace; Web and Mobile use interfaces adapted to their platforms."}

## The same Server versus a different Server

To continue on another device, connect to the same Server and select the same Project and conversation. Saved history is available there; recent output may need time to persist or a state check after reconnecting.

Different Servers keep separate configuration and records. If a Project disappears after switching Servers, check the address and identity before recreating it.

## Get started

1. Initialize the Server and make sure the device can reach its address.
2. Sign in to Web with the administrator password or a [third-party account]({{< relref "/docs/features/oidc-login" >}}); connect Desktop and Mobile with API Keys.
3. Confirm the Server and Project, then open an existing conversation or create one.
4. Check history and execution status to avoid starting the same task again after switching devices.

Tasks run on the execution host. Connecting from a phone or browser does not move execution to that device. Desktop Full includes a Server; Desktop Client connects to an existing one. Mobile currently offers a source-based setup. Layouts and management entry points vary across clients.

[Read the client connection guide]({{< relref "/docs/guides/clients" >}}) · [Install and configure Server]({{< relref "/docs/start/install" >}})
