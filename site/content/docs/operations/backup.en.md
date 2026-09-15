---
title: "Data, backups, and upgrades"
description: "Back up databases and keys, and manage project workspaces separately."
weight: 40
lastmod: 2026-09-15
translationKey: docs/operations/backup
---

A complete backup includes AGW configuration and records, encryption keys, and project files. Copying only the installation directory or code repository can leave out data needed to restore the service.

Locate the actual database, data directory, and Project workspaces first. Use the defaults below as a guide and confirm them against the running configuration.

## What to preserve

`AgwDataDir` defaults to `~/agw`; Docker uses `/data`. Paths expand `~`; other relative paths resolve from the process working directory.

Preserve together:

- The database, including `setting`, `api_token`, conversations, and execution records.
- `keys/` and `skills/` under the data directory.
- Deployment configuration and secret references, with actual secrets backed up in secure storage.
- Business files in each primary and additional Project directory, using a separate file-backup strategy.

Logs are independent of the data directory. Logs and temporary files are not required for authentication recovery. Losing Data Protection keys can make protected credentials unreadable.

## Make a backup inventory

Record the database location, resolved `AgwDataDir`, all Project directories, and current application version. Include the hidden `.agw/memory/` directory for workspace-based Memory; database-based Memory is included in the database backup.

Wait for tasks to finish or interrupt them before backing up, so they do not keep changing files. For a simple SQLite deployment, stop Server before copying the database and related files. Use PostgreSQL’s backup tools for PostgreSQL. Data and workspaces may be in different locations; check each rather than assuming everything is under `/data`.

## Upgrade sequence

1. Read the target Release notes and record the current image or package version.
2. Take a consistent backup: stop Server before copying a simple SQLite installation, or use PostgreSQL's database backup mechanism.
3. Update Server and clients while retaining data, keys, and directory mounts.
4. Verify initialization state, login, Project files, and a small task. For split deployments, also verify workers and a Job.

Pre-1.0 upgrades may include schema changes. Rollback requires a mutually compatible application version, database backup, and key backup.

## Verify recovery

Test restoration in an isolated environment first, including credential decryption and file visibility. Data or log root changes require restarting and moving existing files yourself; AGW does not relocate them automatically.

## Implementation and references

- [Backup and data paths](https://github.com/zxyao145/agw/blob/main/docs/4.Deployment.md)
