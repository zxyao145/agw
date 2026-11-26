---
title: "Use extensions"
description: "Install, enable, and manage Desktop and Server extensions, file viewers, and themes."
weight: 105
lastmod: 2026-11-26
translationKey: docs/guides/extensions
---

AGW supports extensions for both the Desktop workspace and the Server. Desktop extensions add local file viewers, workspace views, and themes. Server extensions add server-side APIs, event handlers, notification channels, and Skills, and can also distribute paired Desktop views.

## Desktop extensions

Desktop ships with bundled extensions enabled on first run. You can disable or uninstall any of them:

| Bundled extension | Purpose |
| --- | --- |
| **Markdown** | Opens `*.md`, `*.markdown`, and text files with **Mixed** (block editor), **Preview** (read-only rendering), and **Code** (Monaco editor with formatting), including Mermaid diagram rendering and text encoding switching |
| **Excalidraw** | Opens, edits, and saves `*.excalidraw` drawings inside file tabs |
| **Image Viewer** | Displays PNG, APNG, JPEG, GIF, WebP, AVIF, BMP, ICO, and SVG images with zoom, drag-to-pan, and pixel/file size details |
| **Bare Repository** | Adds a **Repositories** sidebar and main view to manage default-branch checkouts and feature worktrees, browse branches, and inspect uncommitted changes |
| **Soft Theme** | Provides soft light and dark themes |
| **Warm Minimalism Theme** | Provides warm light and dark themes with a workspace backdrop |

### Install and manage

Prerequisites: open AGW Desktop. Installing from a local folder or `.zip` archive does not require a remote Server connection.

1. Open **Settings → Extensions** (filter by themes to view theme extensions only).
2. Choose an installation source:
   - **Folder**: select an extension directory containing `manifest.json`.
   - **`.zip` archive**: select a packaged extension archive.
   - **Connected Server**: download and install the Desktop view package published by an enabled combined Server extension (such as TaskPilot or Feishu).
3. Enable the extension. Desktop opens a dedicated confirmation window listing all requested permissions; high-risk permissions (local file access, cloud file writes, repository operations, execution sessions, A2A, chat traffic observation, and clipboard reads) are highlighted. Review and confirm.
4. Verify the extension surface:
   - Extensions with Ribbon items or views appear in the left Ribbon or main workspace tabs.
   - File viewers appear to the right of the file name when opening matching file patterns; switch viewers there or use the **More** menu to set the default viewer for that file type.
   - Themes appear under **Settings → Appearance**, alongside System, Light, and Dark modes.

From an extension's details panel, you can **Reload**, disable, or **Uninstall** it (optionally deleting its data folder), or open the extensions and data folders. If an extension disrupts the UI, choose **Reload without extensions** from the tray menu to enter safe mode, then click **Resume extensions** on the Extensions page to resume.

## Server extensions

Server extensions run inside AGW Hosts. Administrators place them under `Extensions:Directory` while Hosts are stopped, then restart to load them. Repository workspace extensions include:

- **TaskPilot**: converts subscribed events into user-owned matters and serialized execution rounds, running an Agent or managing GitHub pull request deliveries through the user's GitHub Connection, with a paired Desktop main view.
- **Feishu**: provides a Feishu notification channel, card actions, and account binding, with a paired Desktop settings view.

### Enable and use

Prerequisites: an administrator has placed the extension folder on every participating Host while stopped and restarted the Hosts. See [Server extensions and ACP hosts]({{< relref "/docs/operations/extensions" >}}) for host setup.

1. Sign in to Web as an administrator and open **System → Server extensions** (`/extensions`).
2. Check the discovered version and current Host runtime state, then enable the extension. Newly discovered extensions start disabled; enablement applies across Hosts.
3. Use the extension's capabilities:
   - Skills registered by enabled extensions are provided automatically during runtime capability composition without importing or binding them on the **Skills** page.
   - Notification channels and event handlers become available in **Events** and **Notifications**.
   - For combined extensions (such as TaskPilot and Feishu), install the Desktop view package from the connected Server under Desktop **Settings → Extensions**, then open it from the Ribbon to configure user settings.

Disabling or removing a Server extension preserves its enablement setting, extension business tables, migration history, and data directory.

## Limitations and next steps

- Desktop extension network access is limited to approved `hostPermissions`. Check the extension details panel for recently blocked requests when troubleshooting.
- Installing, updating, or removing Server extensions requires stopping all participating Hosts first.
- For host installation and restart procedures, see [Server extensions and ACP hosts]({{< relref "/docs/operations/extensions" >}}).
- To build your own extension, see [Build Server and Desktop extensions]({{< relref "/docs/development/extension-sdk" >}}).

## Implementation and references

- [Desktop extension runtime](https://github.com/zxyao145/agw/blob/main/src/clients/desktop/README.md)
- [Server extension SDK](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Extensions.Sdk/README.md)
- [TaskPilot extension](https://github.com/zxyao145/agw/blob/main/src/extensions/taskpilot/README.md)
- [Feishu extension](https://github.com/zxyao145/agw/blob/main/src/extensions/feishu/README.md)
