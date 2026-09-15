---
title: "Configure integrations"
description: "Create your own Connection from an available integration and bind it to an agent."
weight: 100
lastmod: 2026-09-15
translationKey: docs/guides/integrations
---

Integrations let custom agents use authorized external accounts, for example to read information from GitHub. Configure several accounts for one service and choose which one an agent uses. The built-in catalog currently provides GitHub.

Prepare the required authentication details and check that the account can access the resources needed for your task.

## From definition to connection

- **Available integrations**: global catalog definitions available for configuration.
- **Configured integrations**: accounts or service endpoints configured by the current user.
- **Connection**: the concrete instance selected and bound; one integration can have several accounts.

1. Select GitHub in Available integrations and complete the setup required by its chosen authentication method.
2. Create a Connection with a clear Alias and complete authentication as requested.
3. Confirm it is Ready, then bind the specific connection to an Agent or Project.
4. Run a read operation in a new custom-agent turn and verify the intended account is used.

Aliases are immutable after creation and unique per user. Tool names follow `{alias}__{operation}` to distinguish accounts.

![AGW Desktop: Configured integrations lists configured accounts; Available integrations lists the integration catalog.](/images/screenshots/integrations.png)
{caption="AGW Desktop: Configured integrations lists configured accounts; Available integrations lists the integration catalog."}

![AGW Desktop: the GitHub integration form. Choose authentication, enter a name and Alias, then save and complete authorization.](/images/screenshots/integration-create.png)
{caption="AGW Desktop: the GitHub integration form. Choose authentication, enter a name and Alias, then save and complete authorization."}

## Ownership and credentials

Installation setup and Connections belong to the current user. Setup changes invalidate only that user's connections. Only owner-matched Ready connections contribute runtime capabilities. Credential access, OAuth, and tool invocation all check ownership.

## Current limits

Remote Plugin Marketplace download, signing, and upgrades are unavailable. Third-party Plugin Skill scripts are not executed. Connections are not injected into external Codex or Claude agents. Connection changes do not imply live mutation of an existing tool list; verify changes in a new turn.

## Implementation and references

- [Integrations](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
