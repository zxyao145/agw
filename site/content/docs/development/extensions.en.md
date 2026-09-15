---
title: "Extend tools and integrations"
description: "Choose capability ownership and use generated tools and user connections."
weight: 40
lastmod: 2026-09-15
translationKey: docs/development/extensions
---

Prerequisites: understand module boundaries and decide whether the capability is general-purpose or business-owned. Begin with one small, verifiable capability.

## Tool extension path

1. Put general-purpose tools in `Agw.Tools`; business tools belong in their module's `Application/Tools`, with DTOs in `Contracts/Tools`.
2. Reference `Agw.Tools.Abstractions`; add `Agw.Tools.Generators` as an Analyzer for attributed declarations.
3. Explicitly declare permissions, argument descriptions, and return types. Standalone tools and attributed containers stay stateless; session state belongs in a Provider, session, or owned storage.
4. Register services and generated declarations in the owning module. Choose Skill exposure or explicit global catalog inclusion.
5. Verify discovery, arguments, permissions, project binding, and error mapping.

The generator emits metadata, JSON Schema, and direct invocation delegates. Do not add runtime reflection scanning as a fallback. Skill tools use `IAgentSkillRegistration.Tools` and bind to a Project during execution. Registering a generated module does not automatically expose every tool globally.

## Integration extension path

`IPluginCatalog` owns Plugin, Connector, authentication, and capability-source definitions. Definitions are code/content assets. User setup is `PluginInstallation`; selectable accounts or endpoints are `Connection`. Do not collapse these into global configuration.

Add the catalog definition and required capability source, then verify per-user setup, Ready state, binding, and invocation. Infrastructure protects and resolves credentials. Reads and execution retain owner checks. Credential injection over HTTP/SSE requires HTTPS.

## Choose global or Skill-owned tools

Expose independently selectable general-purpose operations explicitly in the global catalog. Register tools used only by a Skill through that Skill, so instructions and tools reach the agent together. For example, `agw-job` supplies job-management tools owned by the Jobs module.

After compilation, verify that the agent can discover the tool. If it is missing, check generated declarations and registration. If invocation fails, check Project binding, permissions, and arguments. Compilation alone does not verify runtime integration.

## Verify

Cover successful invocation, invalid arguments, insufficient permissions, foreign Connections, and non-Ready Connections. Keep compile-time diagnostics effective. Use fake services rather than real accounts or external CLIs in tests.

## Implementation and references

- [Tool abstractions and examples](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools.Abstractions/README.md)
- [Integrations](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
