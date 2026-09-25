---
title: "扩展 Tools 与 Integrations"
description: "选择能力归属，使用编译期工具声明与用户连接。"
weight: 40
lastmod: 2026-09-25
translationKey: docs/development/extensions
---

扩展前先确定要增加什么：一个具体操作可以写成 Tool，一组任务说明和专属工具可以通过 Skill 提供，需要用户连接外部账号的能力则适合 Integration。

先阅读[模块边界]({{< relref "/docs/development/architecture" >}})，确认能力由哪个模块负责。本页说明放置代码、注册能力和验证调用的顺序；具体声明写法可参考文末的工具示例。

## 工具扩展路径

1. 手写的 `IAgwTool`、`IContextualTool` 和 `IToolBlock` 实现放在 `Agw.Tools`，全局目录只扫描这个程序集中的手写工具。业务工具放在所属模块的 `Application/Tools`，以 Attribute 容器声明或通过 Skill 提供，DTO 放 `Contracts/Tools`。
2. 引用 `Agw.Tools.Abstractions`，需要 Attribute 声明时将 `Agw.Tools.Generators` 作为 Analyzer 引用。
3. 显式声明权限、参数说明和返回类型。独立工具及使用 Attribute 声明的工具容器不能保存会话状态；状态放在 Provider、会话对象或所属存储中。
4. 在所属模块注册所需服务与生成声明。选择通过 Skill 提供，或显式加入全局目录。
5. 验证工具发现、参数、权限、项目绑定和错误映射。

生成器输出元数据、JSON Schema 和直接调用委托。不要加入运行时反射扫描兜底。Skill 工具有两个来源：`IAgentSkillRegistration.Tools` 提供手写的 `IProjectScopedAgwTool` 实例，`ToolTypes` 提供 Attribute 容器类型（由 `IAgwToolSet<T>` 生成）。它们在执行时绑定 Project，不因注册生成模块就自动进入全局目录。

## 集成扩展路径

`IPluginCatalog` 拥有 Plugin、Connector、认证和能力源定义。定义是代码/内容资产；用户设置是 `PluginInstallation`，可选账号或端点是 `Connection`。不要将它们合成一张全局配置表。

先增加目录定义和必要的工具来源，再验证用户完成设置、账号进入 Ready 状态、绑定 Agent 和实际调用的全过程。凭据由 Infrastructure 加密保存并在调用时读取；读取和执行都要检查账号归属。通过 HTTP/SSE 发送凭据时使用 HTTPS。

## 选择全局工具还是 Skill 专属工具

如果工具是可单独选用的通用操作，可显式加入全局目录。如果它只服务于某个 Skill，就通过该 Skill 注册，使说明和工具一起提供给 Agent。例如，`agw-job` 的任务管理工具属于 Jobs 模块，并随 Skill 提供。

工具成功编译后，还要确认 Agent 实际能发现它。若目录中没有出现，应先检查生成声明和注册位置；若调用时失败，再检查 Project 绑定、权限及参数。编译通过并不等于已经完成运行时接入。

## 方式一：通过接口定义 Tool

适合一个类负责一个可独立调用的操作。实现 `IAgwTool`，声明稳定名称、分类、Plan 可用性和权限，并实现唯一必需的成员 `ToAITool()`。仓库工具的写法是把操作放在 `Execute` 方法中，再在 `ToAITool()` 中用 `AgwAIFunctionFactory.CreateParameterObjectFunction` 包装成模型可调用的函数；这个工厂是 `Agw.Tools` 的内部类型，所以示例放在 `Agw.Tools` 中。下面是一个不读写外部资源的最小示例：

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

`IAgwTool` 继承元数据接口 `IAgwToolMeta`，`ToAITool()` 把执行方法包装成模型可调用的函数。参数 DTO 和方法上的 `Description` 帮助模型理解何时调用、如何填参。实际异步 I/O 应参考仓库工具使用异步方法并传递 `CancellationToken`；业务 DTO 放在所属模块的 `Contracts/Tools`。

### 接入与使用

1. 将实现放到 `Agw.Tools/Impl/Tools`。全局目录只扫描 `Agw.Tools` 程序集中的手写工具，放在业务模块中的 `IAgwTool` 不会被发现；业务模块改用方式二的 Attribute 容器，或把 `IProjectScopedAgwTool` 实例放进 Skill 的 `IAgentSkillRegistration.Tools`。声明对象保持无状态，不在字段中保存当前 Project、用户或会话数据。
2. 在 `src/server/Agw.Shared/Tooling/ToolValueObject.cs` 中把工具名称加入 `ToolDefinitionNames` 及其 `All` 列表，并加入具体 `ToolDefinition`、`[JsonDerivedType]` 名称映射和空的或实际的 Options 类型，保持定义与实现一一对应。名称没有登记时，注册会报错 “does not have a registered ToolDefinition”。
3. 在所属模块 DI 入口登记依赖，确认工具出现在 `/api/tools`；工具绑定到 Agent 或 Project 后，才供该执行目标使用。
4. 在 Chat 让 Agent 调用该操作，检查输入和输出。直接调用 C# 方法适合单元测试，但不能证明运行时权限和 Project 绑定已接通。

有 Project 或运行时目录依赖的独立工具，应参考 `IContextualTool.MaterializeAsync`（接口位于 `Agw.Tools/Contracts/Abstractions`，与其他手写工具一样只从 `Agw.Tools` 程序集发现），把已校验的上下文绑定到本次贡献的函数中。不要让模型通过一个随意填写的 Project ID 决定资源归属。

## 方式二：通过 Attribute 定义 Tool

适合把一个服务中的多个操作声明为工具，也适合随 Skill 提供的业务能力。先添加抽象项目和生成器引用，路径按项目位置调整：

```xml
<ItemGroup>
  <ProjectReference Include="../Agw.Tools.Abstractions/Agw.Tools.Abstractions.csproj" />
  <ProjectReference Include="../Agw.Tools.Generators/Agw.Tools.Generators.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

下面与接口示例实现同一个 `echo` 操作，**二选一使用**，不要同时注册同名工具：

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

示例容器是普通的 `sealed class`，其中的方法都是静态方法；后面的 `IAgwToolSet<TextTools>` 要求类型参数是非静态类，所以容器不能声明为 `static class`。`AgwToolContainer` 批量选择类型直接声明的 public 普通方法；`AgwTool` 指定名称和权限；`AgwToolIgnore` 排除辅助方法。没有显式名称时，默认使用方法名并移除末尾的 `Async`。容器及每个操作都应明确声明或继承权限；`AllowInPlanMode` 是独立设置。

实例容器使用显式构造函数注入，并在 DI 中注册实例类型。静态方法可通过标记 `[AgwToolService]` 的参数取得服务。服务参数与 `CancellationToken` 不进入模型可填写的参数 Schema；每次调用都有独立的异步 DI scope。

### 生成、注册与选用

编译器生成元数据、输入输出 Schema 和直接调用委托。以程序集 `My.Module` 为例，在模块组合入口登记生成模块；需要全局选用时再选择容器类型：

```csharp
// Example assembly name: My.Module
services.AddSingleton<IAgwGeneratedToolModule>(
    Agw.Generated.My.Module.AgwToolModule.Instance);
// Only for tools intended for the global catalog:
services.AddToolCatalogTypes(typeof(TextTools));
```

上述代码片段需要生成声明命名空间及所属注册扩展的引用。只包含静态方法的容器不必登记实例；包含实例方法的容器还需 `services.AddScoped<YourToolContainer>()`。生成模块只登记声明，不会自动公开全部工具。全局工具仍要完成具体 ToolDefinition 和 JSON 多态映射。

若工具只服务于 Skill，让 Skill 注册类使用 `partial` 并实现 `IAgwToolSet<TextTools>`，生成器补齐 `ToolTypes`；同时按 `IAgentSkillRegistration` 完成 Id、说明和创建逻辑。在所属模块的 DI 入口用 `services.AddSingleton<IAgentSkillRegistration, YourSkillRegistration>()` 注册 Skill，并登记生成模块和实例容器，写法参考 Jobs 模块的 `JobManagementSkillRegistration`。将该 Skill 绑定到 Agent/Project 后，其工具才参与运行时组合。手写的 Skill 工具实现 `IProjectScopedAgwTool`，放进 `IAgentSkillRegistration.Tools`，不应额外放进全局目录。

生成失败时先处理编译诊断：不支持的签名是 `AGWTOOL001`，无效声明是 `AGWTOOL002`。不要用运行时反射绕过诊断。完整的实例容器和 Skill 示例见 [工具抽象说明](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools.Abstractions/README.md)。

## ToolBlock：定义共享状态的一组工具

当多个操作需要维护同一份状态，例如添加 Todo、完成 Todo 和查看待办列表，应把它们作为一个 ToolBlock 整组选用。Attribute 容器只是声明方式，本身不提供会话状态隔离。

下面是仓库的 `TodoToolBlock` 完整实现：

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

### Todo 的状态与生命周期

- `Descriptor.Members` 声明五个成员、各自权限和 Plan 可用性。成员不能单独注册成全局工具。
- `MaterializeAsync` 创建本次能力组合使用的 `AgwTodoProvider`，放进 `ToolContribution.ContextProviders`，同时提供循环检查器。
- `AgwTodoProvider` 把 `AgwTodoState` 保存到当前 `AgentSession.StateBag`，并通过 `StateKeys` 声明状态键。调用时从当前 session 读取、修改并保存，不能用 static 列表或共享单例保存所有人的 Todo。
- 同一 session 的后续调用可继续使用已有 Todo；不同 session 的状态分开。持久化恢复依赖外层会话保存与恢复流程，不能仅因为 Provider 中有字段就认为状态可以恢复。
- `TodoCompletionLoopEvaluator` 检查待办是否完成；与 Mode 一起启用时只在 Execute 阶段参与检查。自定义 ToolBlock 不一定需要循环检查器，按具体任务添加。

### 开发自己的有状态 ToolBlock

1. 定义数据对象和状态范围：回合、会话或项目长期存储。会话状态参考 `AgwTodoState`，项目长期状态参考 Project Memory。
2. 在 `Agw.Shared/Tooling/ToolValueObject.cs` 中把名称加入 `ToolBlockDefinitionNames` 及其 `All` 列表，增加具体 `ToolBlockDefinition`、Options 和 `[JsonDerivedType]` 名称映射，再在 `ToolBlockNames` 中加入引用该常量的运行时名称。启动时的覆盖检查会拒绝缺少定义或缺少实现的 ToolBlock。
3. 实现 `IToolBlock`，一次声明全部成员，明确每个成员的权限与 `allowInPlanMode`。
4. 在 `MaterializeAsync` 创建 Provider，将函数和状态操作绑定到当前上下文；把生命周期交给 `ToolContribution`，不要缓存跨用户的 Provider 或 scoped 服务。
5. 在目录和定义解析流程接通整组选用，再测试：新增、完成、移除、不同 session 隔离、保存恢复、Plan 限制与审批。

参考 [Todo Provider](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools/Impl/ToolBlocks/Todo/AgwTodoProvider.cs) 和 [Todo 状态](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools/Impl/ToolBlocks/Todo/AgwTodoState.cs)，不要只复制描述符而遗漏状态的读取与保存。

## Plugin：从目录定义到实际调用

这里的 Plugin 是 AGW 集成目录中的代码与内容定义。当前内建示例是 GitHub；新增 Plugin 需要修改并构建服务端，不是上传任意插件包即可运行。界面用 **Available integrations** 展示目录，用 **Configured integrations** 展示用户配置的账号或端点。

| 开发对象 | 定义内容 |
| --- | --- |
| `PluginDefinition` | 稳定 Id、版本、显示名、Connector 和可选 Skill 内容 |
| `ConnectorDefinition` | 服务或协议变体，例如 GitHub Cloud |
| `AuthSchemeDefinition` | 认证方式、用户配置字段及 OAuth 流程设置 |
| `CapabilitySourceDefinition` | 工具由内部 C# Provider 创建，或从 MCP 服务取得 |
| `PluginInstallation` | 当前用户的安装设置，例如 OAuth Client ID/Secret |
| `Connection` | 当前用户配置的具体账号、凭据和 Ready 状态，绑定时使用 ConnectionId |

### 定义目录与认证

下面是现有 GitHub 目录实现，可作为开发新集成时的完整结构参考。OAuth 地址、scope 和字段需按目标服务的协议设置，不能照搬 GitHub 的值：

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

新增定义时保持 Plugin、Connector、认证方式和能力源的 Id 稳定，并让 `PluginCatalogValidator` 验证整个目录。`InstallationFields` 用于每用户的安装配置；账号级字段应放到相应认证方案的连接字段中。秘密字段使用 Secret 类型，不能把真实凭据写入目录定义。

### 实现能力源

**Native**：定义中的 `Provider = "github"` 对应 `IConnectionNativeCapabilityProvider.Provider`。实现 `CreateTools(ConnectionNativeCapabilityContext)`，用已解析的 ConnectionId、Alias 和 ProjectId 创建工具。工具名称通常为 `{alias}__{operation}`，防止多个账号的工具混淆。

参考 `GitHubConnectionNativeCapabilityProvider`：生成函数绑定账号和 Project，实际调用时创建 scope、解析 `IGitHubConnectionInvoker`；Invoker 再执行账号归属、Ready 状态与凭据检查。不得让模型指定任意 ConnectionId，或在单例 Provider 中缓存账号密钥。为新增操作接入当前能力源的权限元数据与执行审批流程。

**MCP**：使用 `McpCapabilitySourceDefinition`，选择 stdio、HTTP 或 SSE Transport，再通过 `CredentialBindings` 把安装字段、连接字段或 OAuth Token 注入所需环境变量或 HTTP Header。经过网络注入凭据时使用 HTTPS；字段引用必须与认证定义匹配。MCP 路径复用连接授权与运行时校验，不是直接把未验证的 URL 交给 Agent。

### 注册、内容与测试

1. 在 Integrations 的 DI 入口维护目录注册；Native Provider 注册为 `IConnectionNativeCapabilityProvider`，调用服务按其生命周期登记，例如 GitHub 的 scoped Invoker。
2. 若附带 Skill，将内容放入 Plugin 内容目录，并通过 `PluginSkillDefinition.ContentPath` 指向 `SKILL.md`。它提供使用说明，不自动执行第三方脚本；同时确认构建产物包含内容文件。
3. 在 Available integrations 找到定义，为测试用户配置安装字段和账号。完成认证后确认 Ready，再绑定 Agent 或 Project。
4. 验证一次读取和一次受控写入，检查工具名称、参数、权限及错误处理。测试使用真实实现，不使用 mock 或 fake 实现；需要真实账号或 OAuth 授权的测试不放进默认测试套件。
5. 覆盖跨用户 ConnectionId、未就绪账号、失效凭据、目录字段错误、同名工具和配置变更。修改用户安装设置只应影响该用户的账号连接。

当前没有远程 Marketplace 的下载、签名与自动升级机制。完整调用链参考 [GitHub Native Provider](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/Tools/GitHub/GitHubConnectionNativeCapabilityProvider.cs)、[GitHub Invoker](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/Tools/GitHub/GitHubConnectionInvoker.cs) 和 [能力源定义](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/Domain/Plugins/CapabilitySourceDefinition.cs)。

## 验证

至少覆盖成功调用、非法参数、权限不足、外来 Connection 和未就绪 Connection。保持编译期诊断有效。测试使用真实实现，不使用 mock 或 fake 实现；依赖真实账号或外部 CLI 的测试需要显式开启，不放进默认测试套件。

## 实现与参考

- [Tool abstractions and examples](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Tools.Abstractions/README.md)
- [Integrations](https://github.com/zxyao145/agw/blob/main/src/server/Agw.Integrations/README.md)
