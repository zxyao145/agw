---
title: "Extend tools and integrations"
description: "Choose capability ownership and use generated tools and user connections."
weight: 40
lastmod: 2026-09-25
translationKey: docs/development/extensions
---

Prerequisites: understand module boundaries and decide whether the capability is general-purpose or business-owned. Begin with one small, verifiable capability.

## Tool extension path

1. Put hand-written `IAgwTool`, `IContextualTool`, and `IToolBlock` implementations in `Agw.Tools`; the global catalog scans only that assembly for hand-written tools. Business tools belong in their module's `Application/Tools`, declared as attributed containers or supplied through a Skill, with DTOs in `Contracts/Tools`.
2. Reference `Agw.Tools.Abstractions`; add `Agw.Tools.Generators` as an Analyzer for attributed declarations.
3. Explicitly declare permissions, argument descriptions, and return types. Standalone tools and attributed containers stay stateless; session state belongs in a Provider, session, or owned storage.
4. Register services and generated declarations in the owning module. Choose Skill exposure or explicit global catalog inclusion.
5. Verify discovery, arguments, permissions, project binding, and error mapping.

The generator emits metadata, JSON Schema, and direct invocation delegates. Do not add runtime reflection scanning as a fallback. Skill tools come from two members: `IAgentSkillRegistration.Tools` supplies hand-written `IProjectScopedAgwTool` instances, and `ToolTypes` supplies attributed container types generated through `IAgwToolSet<T>`. They bind to a Project during execution. Registering a generated module does not automatically expose every tool globally.

## Integration extension path

`IPluginCatalog` owns Plugin, Connector, authentication, and capability-source definitions. Definitions are code/content assets. User setup is `PluginInstallation`; selectable accounts or endpoints are `Connection`. Do not collapse these into global configuration.

Add the catalog definition and required capability source, then verify per-user setup, Ready state, binding, and invocation. Infrastructure protects and resolves credentials. Reads and execution retain owner checks. Credential injection over HTTP/SSE requires HTTPS.

## Choose global or Skill-owned tools

Expose independently selectable general-purpose operations explicitly in the global catalog. Register tools used only by a Skill through that Skill, so instructions and tools reach the agent together. For example, `agw-job` supplies job-management tools owned by the Jobs module.

After compilation, verify that the agent can discover the tool. If it is missing, check generated declarations and registration. If invocation fails, check Project binding, permissions, and arguments. Compilation alone does not verify runtime integration.

## Option 1: define a Tool with an interface

Use `IAgwTool` for one independently callable operation per class. Declare a stable name, category, Plan availability, and permission, and implement the one required member, `ToAITool()`. Repository tools put the operation in an `Execute` method and wrap it in `ToAITool()` with `AgwAIFunctionFactory.CreateParameterObjectFunction`. That factory is internal to `Agw.Tools`, so the example lives in `Agw.Tools`. This minimal example performs no external I/O:

```csharp
using System.ComponentModel;
using Agw.Tools.Abstractions;
using Agw.Tools.Infrastructure;
using Microsoft.Extensions.AI;

public sealed class EchoInput
{
    [Description("Text to return unchanged.")]
    public string Text { get; set; } = "";
}

public sealed class EchoTool : IAgwTool
{
    public string Name => "echo";
    public string Category => "Examples";
    public bool AllowInPlanMode => true;
    public AgwToolPermission RequiredPermission => AgwToolPermission.None;

    [Description("Return the supplied text unchanged.")]
    public string Execute(EchoInput input) => input.Text;

    public AITool ToAITool()
    {
        Func<EchoInput, string> func = Execute;
        return AgwAIFunctionFactory.CreateParameterObjectFunction(func, Name);
    }
}
```

`IAgwTool` extends the metadata interface `IAgwToolMeta`; `ToAITool()` wraps the execution method as a model-callable function. Describe the method and input DTO so the model knows when and how to call it. Use asynchronous methods and propagate CancellationToken for real I/O. Business DTOs belong in the owner module’s Contracts/Tools.

### Register and use it

1. Put the implementation in `Agw.Tools/Impl/Tools`. The global catalog scans only the `Agw.Tools` assembly for hand-written tools, so an `IAgwTool` placed in a business module is never discovered. Business modules use Option 2's attributed containers instead, or place `IProjectScopedAgwTool` instances in a Skill's `IAgentSkillRegistration.Tools`. Keep declarations stateless; do not store the current user, Project, or conversation in fields.
2. In `src/server/Agw.Shared/Tooling/ToolValueObject.cs`, add the tool name to `ToolDefinitionNames` and its `All` list, plus a concrete `ToolDefinition`, a `[JsonDerivedType]` name mapping, and an empty or real Options type. Definitions and implementations must match one to one. An unregistered name fails registration with “does not have a registered ToolDefinition”.
3. Register dependencies through the owner module’s DI entry point. Verify /api/tools and bind the tool to an Agent or Project before using it.
4. Ask the agent to invoke it and inspect arguments and output. Direct C# calls test the operation but do not verify runtime permission or Project binding.

For standalone tools requiring a Project or runtime directory, use IContextualTool.MaterializeAsync (defined in `Agw.Tools/Contracts/Abstractions` and, like other hand-written tools, discovered only in the `Agw.Tools` assembly) and bind authorized context into contributed functions. Do not let a model-supplied Project ID determine resource ownership.

## Option 2: define Tools with attributes

Attributes work well for multiple operations in a service or business capabilities supplied by a Skill. Add these project references, adjusting relative paths:

```xml
<ItemGroup>
  <ProjectReference Include="../Agw.Tools.Abstractions/Agw.Tools.Abstractions.csproj" />
  <ProjectReference Include="../Agw.Tools.Generators/Agw.Tools.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

This implements the same echo operation as the interface example. Choose one implementation; registering both creates a name collision.

```csharp
using System.ComponentModel;
using Agw.Tools.Abstractions;
using Agw.Tools.Abstractions.Attributes;

[AgwToolContainer(AgwToolPermission.None,
    DefaultCategory = "Examples", AllowInPlanMode = true)]
public sealed class TextTools
{
    [AgwTool("echo", AgwToolPermission.None)]
    [Description("Return the supplied text unchanged.")]
    public static string Echo([Description("Text to return.")] string text)
    {
        return text;
    }

    [AgwToolIgnore]
    public static string FormatForDisplay(string text) => text.Trim();
}
```

The example container is an ordinary `sealed class` whose methods are all static. `IAgwToolSet<TextTools>`, used later, requires a non-static class as its type argument, so the container cannot be a `static class`. AgwToolContainer selects public ordinary methods declared directly on the type. AgwTool supplies a name and permission; AgwToolIgnore excludes helpers. Default names use the method name minus a terminal Async. Permissions must be explicitly declared or inherited from the container; AllowInPlanMode is independent.

Instance containers use explicit constructor injection and must be registered in DI. Static methods can use parameters marked AgwToolService. Service and CancellationToken parameters are excluded from model-facing schemas. Each invocation gets an independent asynchronous DI scope.

### Generate, register, and select

Compilation generates metadata, input/output schemas, and direct invocation delegates. For an assembly named My.Module, register its generated module, then explicitly select container types if they belong in the global catalog:

```csharp
// Example assembly name: My.Module
services.AddSingleton<IAgwGeneratedToolModule>(
    Agw.Generated.My.Module.AgwToolModule.Instance);
// Only for tools intended for the global catalog:
services.AddToolCatalogTypes(typeof(TextTools));
```

Import the generated contracts and relevant registration extensions. Containers with only static methods need no instance registration; containers with instance methods also need AddScoped<YourToolContainer>(). A generated module registers declarations without exposing every tool globally. Global tools still require concrete ToolDefinition types and JSON mappings.

For Skill-only tools, make the registration partial and implement IAgwToolSet<TextTools> so the generator supplies ToolTypes. Complete the remaining IAgentSkillRegistration members, including identity, description, and creation logic. Register the Skill in the owner module's DI entry point with `services.AddSingleton<IAgentSkillRegistration, YourSkillRegistration>()`, along with its generated module and instance containers; see `JobManagementSkillRegistration` in the Jobs module. Bind the Skill to an Agent/Project to contribute its tools. Hand-written Skill tools implement `IProjectScopedAgwTool` and go in IAgentSkillRegistration.Tools; they do not need global catalog entries.

Fix generator diagnostics rather than adding reflection fallbacks: AGWTOOL001 identifies unsupported signatures; AGWTOOL002 identifies invalid declarations. See the [tool abstraction guide](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools.Abstractions/README.md) for complete instance-container and Skill examples.

## ToolBlocks: tools with shared state

Use a ToolBlock when operations such as adding, completing, and listing todos must maintain one coherent state. Members are selected as a group. An attribute container is a declaration mechanism, not session-state isolation.

Here is the complete repository TodoToolBlock:

```csharp
using Agw.Tools.ToolBlocks;

namespace Agw.Tools.Impl.ToolBlocks.Todo;

public sealed class TodoToolBlock : IToolBlock
{
    public ToolBlockDescriptor Descriptor { get; } =
        new(
            ToolBlockNames.Todo,
            "Todo",
            "Tracks multi-step work with a persistent todo list.",
            ToolBlockScope.Agent | ToolBlockScope.Project,
            [
                new("todos_add", AgwToolPermission.None, allowInPlanMode: true),
                new("todos_complete", AgwToolPermission.None, allowInPlanMode: true),
                new("todos_remove", AgwToolPermission.None, allowInPlanMode: true),
                new("todos_get_remaining", AgwToolPermission.ReadOnly, allowInPlanMode: true),
                new("todos_get_all", AgwToolPermission.ReadOnly, allowInPlanMode: true),
            ]
        );

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        var contribution = new ToolContribution();
        contribution.PlanModeAllowedToolNames.UnionWith(
            Descriptor.Members.Where(static member => member.AllowInPlanMode).Select(static member => member.Name)
        );
        contribution.ContextProviders.Add(new AgwTodoProvider());
        var evaluatorOptions = context.EnabledToolBlockNames.Contains(ToolBlockNames.Mode)
            ? new TodoCompletionLoopEvaluatorOptions { Modes = ["execute"] }
            : null;
        contribution.LoopEvaluators.Add(new TodoCompletionLoopEvaluator(evaluatorOptions));
        return ValueTask.FromResult(contribution);
    }
}
```

### Todo state and lifetime

- Descriptor.Members declares all five members, permissions, and Plan availability. Members cannot be separately registered as global tools.
- MaterializeAsync creates an AgwTodoProvider in ToolContribution.ContextProviders and adds a loop evaluator.
- AgwTodoProvider keeps AgwTodoState in the current AgentSession.StateBag and declares its key through StateKeys. Operations read, modify, and save the current session’s state; a static list or singleton must not hold everyone’s todos.
- Later calls in the same session reuse state; different sessions are isolated. Durable recovery depends on the surrounding session save/restore pipeline, not merely storing data in a Provider field.
- TodoCompletionLoopEvaluator checks outstanding items. When Mode is enabled, it evaluates in Execute mode. Custom ToolBlocks only need an evaluator if their behavior requires one.

### Implement your own stateful ToolBlock

1. Define the data and its lifetime: turn, session, or persistent project storage. Use AgwTodoState for session state and Project Memory for project storage as references.
2. In `Agw.Shared/Tooling/ToolValueObject.cs`, add the name to `ToolBlockDefinitionNames` and its `All` list, plus a concrete ToolBlockDefinition, Options, and `[JsonDerivedType]` mapping. Then add a runtime name in ToolBlockNames that references that constant. The startup coverage check rejects ToolBlocks missing a definition or an implementation.
3. Implement IToolBlock and declare every member’s permission and allowInPlanMode.
4. Create Providers in MaterializeAsync, bind functions to the current context, and transfer lifetime ownership to ToolContribution. Do not cache Providers or scoped services across users.
5. Wire group selection into catalog and definition resolution. Test adding, completing, removing, session isolation, save/restore, Plan restrictions, and approvals.

Read the [Todo Provider](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools/Impl/ToolBlocks/Todo/AgwTodoProvider.cs) and [Todo state](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools/Impl/ToolBlocks/Todo/AgwTodoState.cs); copying the descriptor alone omits state loading and saving.

## Plugins: from catalog definition to invocation

A Plugin here is code and content in AGW’s integration catalog. GitHub is the built-in example. Adding a Plugin requires changing and building the server; arbitrary uploaded packages are not executed. Available integrations shows catalog definitions; Configured integrations shows user accounts or endpoints.

| Developer object | Responsibility |
| --- | --- |
| PluginDefinition | Stable ID, version, display name, connectors, optional Skill content |
| ConnectorDefinition | Service or protocol variant, such as GitHub Cloud |
| AuthSchemeDefinition | Authentication method, configuration fields, OAuth settings |
| CapabilitySourceDefinition | Internal C# tools or tools obtained from MCP |
| PluginInstallation | Per-user setup, such as OAuth client ID/secret |
| Connection | User account, credentials, Ready state; bindings use ConnectionId |

### Define the catalog and authentication

Use this existing GitHub catalog as a complete structural reference. Replace OAuth endpoints, scopes, and fields with those required by your target service:

```csharp
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Domain.Plugins;

namespace Agw.Integrations.Infrastructure.Plugins;

/// <summary>
/// 所有可以使用的 plugin 列表
/// </summary>
public sealed class BuiltInPluginCatalog : IPluginCatalog
{
    private static readonly IReadOnlyList<PluginDefinition> Plugins =
    [
        new PluginDefinition
        {
            Id = "github",
            Version = "1.0.0",
            DisplayName = "GitHub",
            Description = "Connect GitHub accounts and use repository capabilities.",
            Tags = ["Git", "Coding"],
            Connectors =
            [
                new ConnectorDefinition
                {
                    Id = "github-cloud",
                    DisplayName = "GitHub Cloud",
                    Description = "Connect a GitHub.com account using OAuth.",
                    AuthSchemes =
                    [
                        new AuthSchemeDefinition
                        {
                            Id = "oauth2",
                            DisplayName = "OAuth 2.0",
                            Type = AuthSchemeType.OAuth2,
                            OAuth2AuthorizationCode = new OAuth2AuthorizationCodeSettings
                            {
                                AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
                                TokenEndpoint = "https://github.com/login/oauth/access_token",
                                UserInfoEndpoint = "https://api.github.com/user",
                                ClientIdFieldId = "client-id",
                                ClientSecretFieldId = "client-secret",
                                SubjectResolution = new OAuthSubjectResolutionDefinition
                                {
                                    Source = OAuthSubjectSource.UserInfo,
                                    Field = "login",
                                },
                                UsePkce = true,
                                ClientAuthenticationMethod = OAuth2ClientAuthenticationMethod.Body,
                                SupportsRefresh = false,
                                Scopes = ["repo", "read:user", "read:org"],
                            },
                            InstallationFields =
                            [
                                new FormFieldDefinition
                                {
                                    Id = "client-id",
                                    Label = "Client ID",
                                    Type = FormFieldType.Text,
                                    IsRequired = true,
                                },
                                new FormFieldDefinition
                                {
                                    Id = "client-secret",
                                    Label = "Client secret",
                                    Type = FormFieldType.Secret,
                                    IsRequired = true,
                                },
                            ],
                        },
                    ],
                    CapabilitySources =
                    [
                        // Agw 内部 C# Provider 创建工具。
                        new NativeCapabilitySourceDefinition { Id = "github-native", Provider = "github" },
                    ],
                },
            ],
            Skills = [new PluginSkillDefinition { ContentPath = "Plugins/github/skills/github/SKILL.md" }],
        },
    ];

    public BuiltInPluginCatalog()
    {
        PluginCatalogValidator.Validate(Plugins);
    }

    public IReadOnlyList<PluginDefinition> List()
    {
        return Plugins;
    }

    public PluginDefinition? Find(string pluginId)
    {
        return Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
    }
}
```

Keep Plugin, Connector, authentication, and source IDs stable. Validate the full catalog with PluginCatalogValidator. InstallationFields describe per-user setup; account fields belong in the authentication scheme’s connection fields. Use Secret field types and never embed real credentials in definitions.

### Implement capability sources

**Native:** the definition’s Provider = "github" matches IConnectionNativeCapabilityProvider.Provider. Implement CreateTools(ConnectionNativeCapabilityContext), binding resolved ConnectionId, Alias, and ProjectId. Use names such as {alias}__{operation} to distinguish accounts.

Follow GitHubConnectionNativeCapabilityProvider: functions bind the account and Project, then create a scope and resolve IGitHubConnectionInvoker when invoked. The Invoker checks ownership, Ready state, and credentials. Do not accept arbitrary model-supplied ConnectionIds or cache account secrets in a singleton. Wire new operations into the source’s permission metadata and approval pipeline.

**MCP:** use McpCapabilitySourceDefinition with stdio, HTTP, or SSE transport. CredentialBindings map installation fields, connection fields, or OAuth tokens to environment variables or HTTP headers. Use HTTPS when sending credentials over the network and keep field references consistent with authentication definitions. This path retains connection authorization and runtime validation rather than passing unchecked URLs to agents.

### Register, package content, and test

1. Maintain catalog registration in the Integrations DI entry point. Register Native providers as IConnectionNativeCapabilityProvider and invocation services with suitable lifetimes, such as the scoped GitHub Invoker.
2. Place optional Skill content in the Plugin content directory and point PluginSkillDefinition.ContentPath to SKILL.md. It provides instructions, not automatic execution of third-party scripts. Ensure published artifacts include these files.
3. Find the definition in Available integrations, configure setup and an account for a test user, complete authentication, verify Ready, and bind it to an Agent or Project.
4. Test a read and a controlled write, including tool names, arguments, permissions, and errors. Tests use real implementations, never mocks or fake implementations; tests that need real accounts or OAuth authorization stay out of the default suite.
5. Cover foreign ConnectionIds, unready accounts, expired credentials, invalid catalog fields, duplicate tool names, and configuration changes. Updating one user’s installation settings must affect only that user’s connections.

There is currently no remote Marketplace download, signature, or automatic upgrade mechanism. Follow the [GitHub Native Provider](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/Tools/GitHub/GitHubConnectionNativeCapabilityProvider.cs), [GitHub Invoker](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/Tools/GitHub/GitHubConnectionInvoker.cs), and [capability-source definitions](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/Domain/Plugins/CapabilitySourceDefinition.cs).

## Verify

Cover successful invocation, invalid arguments, insufficient permissions, foreign Connections, and non-Ready Connections. Keep compile-time diagnostics effective. Tests use real implementations, never mocks or fake implementations; tests that depend on real accounts or external CLIs are opt-in and stay out of the default suite.

## Implementation and references

- [Tool abstractions and examples](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools.Abstractions/README.md)
- [Integrations](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
