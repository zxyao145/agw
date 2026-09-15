---
title: "Projects, files, and workspaces"
description: "Set primary and additional directories and understand file browsing versus the agent’s working directory."
weight: 50
lastmod: 2026-09-15
translationKey: docs/guides/projects
---

A Project keeps a task’s directories, background information, and conversations together. For a code repository, you might create separate conversations for code exploration and troubleshooting while using the same workspace.

The directory must exist and be accessible to the account running AGW Server. With a remote Server, enter a path on that host. With Docker, enter the path inside the container.

## Configure the workspace

1. Create a Project and set its primary `Workspace`. Empty values default to `~/.agw/projects/{projectId:N}`.
2. Add Additional directories for other browsing roots. Each association has a stable ID; changing its path creates a new ID.
3. Switch roots in the file browser dropdown and verify file and Git access.
4. Run an agent in the Project and verify that it uses the intended primary working directory.

Switching the browsing root does not change the agent’s working directory. Removing an additional-directory association does not delete files. Mount network storage through the OS or container platform first, then configure the mounted path as the workspace.

![AGW Desktop: configure a Project’s primary workspace and additional directories. Replace the example paths with real directories on the execution host.](/images/screenshots/project-directories.png)
{caption="AGW Desktop: configure a Project’s primary workspace and additional directories. Replace the example paths with real directories on the execution host."}

## Example: code and reference directories

Suppose code is in `/work/app` and reference material is in `/work/reference`. Set the former as Workspace and add the latter as an additional directory. Browsing reference material in Files leaves the agent’s default working directory at `/work/app`. Tell the agent where the reference material is and give it the required reading capability.

For Docker, mount the directories first. If the host’s `/home/me/app` is mounted at `/work/app` in the container, enter `/work/app` as Workspace. Entering a path in the form does not create a container mount.

In the default path, `{projectId:N}` means the Project ID without hyphens. AGW fills this in automatically; do not enter the placeholder literally.

## When changes apply

Project updates invalidate the local filesystem cache and refresh file browsing immediately. Agents capture immutable directory snapshots at the start of each turn. Changes rebuild the runtime on the next turn while preserving conversation identity. Active turns, child execution, and durable recovery retain their captured paths.

Every distributed execution node must see the same captured host paths. An unavailable or foreign additional directory fails without falling back to the primary root.

## Troubleshoot

For missing files, verify the selected browsing root, mount, execution account, and Server-side path. A different directory in your local terminal is not sufficient evidence. Non-built-in Projects can be copied; recheck directory associations afterward.

## Implementation and references

- [Filesystem resolver](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Files/Application/Storage/Resolver/ProjectScopedFileSystemResolver.cs)
- [Directory snapshots](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Agents.Execution/README.md)
