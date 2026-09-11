# Agw.Tools.Abstractions

`Agw.Tools.Abstractions` 定义 Agw Tool 的轻量契约，包括 Attribute、权限枚举、编译期生成清单，以及生成代码与运行时之间的调用接口。业务模块可以只引用本项目和 `Agw.Tools.Generators`，无需依赖 `Agw.Tools` 的 Registry、ASP.NET Core 接口或具体运行时实现。

Attribute Tool 的完整链路分为三层：

| 项目 | 职责 |
| --- | --- |
| [`Agw.Tools.Abstractions`](.) | 提供 Attribute、`AgwToolPermission`、生成清单和调用桥接契约 |
| [`Agw.Tools.Generators`](../Agw.Tools.Generators) | 在编译期发现方法，生成元数据、JSON Schema、直接调用委托，并补齐 `IAgwToolSet<T>` partial 类型 |
| [`Agw.Tools`](../Agw.Tools) | 在运行时组合生成清单，创建 DI scope，绑定参数，执行权限和 Plan Mode 策略，并投影到 Tool Registry |

这条路径采用严格的编译期生成模式。生成失败会通过编译诊断报告，不会退回到运行时反射。生成的调用代码不会使用 `MethodInfo.Invoke`，运行时也不会再次扫描 Attribute、读取 XMLDoc 或推断方法 Schema。

## 快速上手

### 1. 引用抽象项目和生成器

业务项目需要正常引用 `Agw.Tools.Abstractions`，并将 `Agw.Tools.Generators` 作为 Analyzer 引用：

```xml
<ItemGroup>
  <ProjectReference Include="..\Agw.Tools.Abstractions\Agw.Tools.Abstractions.csproj" />
  <ProjectReference
    Include="..\Agw.Tools.Generators\Agw.Tools.Generators.csproj"
    OutputItemType="Analyzer"
    ReferenceOutputAssembly="false" />
</ItemGroup>
```

`OutputItemType="Analyzer"` 让编译器执行生成器；`ReferenceOutputAssembly="false"` 避免生成器和 Roslyn 成为生产运行时依赖。引用路径应按消费项目的位置调整。

### 2. 声明 Tool 容器

下面的容器使用构造函数注入业务服务，公开两个 Tool，并排除一个普通辅助方法：

```csharp
using System.ComponentModel;
using Agw.Tools.Abstractions;
using Agw.Tools.Abstractions.Attributes;
using Agw.Tools.Abstractions.Generated;

[AgwToolContainer(
    AgwToolPermission.ReadOnly,
    DefaultCategory = "Time",
    AllowInPlanMode = true)]
internal sealed class ClockTools
{
    private readonly TimeProvider _timeProvider;

    public ClockTools(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// 获取当前 UTC 时间。
    /// </summary>
    public DateTimeOffset GetUtcNow()
    {
        return _timeProvider.GetUtcNow();
    }

    [AgwTool("unix_time_seconds", AgwToolPermission.ReadOnly)]
    [Description("获取当前 Unix 时间戳，单位为秒。")]
    public long GetUnixTimeSeconds()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeSeconds();
    }

    [AgwToolIgnore]
    public string FormatForDisplay(DateTimeOffset value)
    {
        return value.ToString("O");
    }
}
```

`AgwToolContainer` 会选择该类型直接声明的 public 普通方法。`FormatForDisplay` 虽然符合批量发现条件，但 `AgwToolIgnore` 的优先级最高，因此不会生成 Tool、Schema 或 ToolBlock 成员。

`ClockTools` 最终生成两个 Tool：

- `GetUtcNow`：使用方法名作为默认名称。
- `unix_time_seconds`：使用 `[AgwTool]` 指定的显式名称。

`FormatForDisplay` 不会出现在生成清单中。

### 3. 注册生成模块和实例类型

每个包含 Attribute Tool 的程序集会生成一个模块单例：

```text
Agw.Generated.<按 namespace segment 规范化的程序集名称>.AgwToolModule.Instance
```

程序集名称中的 `.` 会保留为 namespace 层级，只在每个 segment 内转换不合法的 C# 标识符字符。数字开头和 C# 保留关键字会增加 `_` 前缀。例如，`Agw.Jobs` 的生成类型是 `Agw.Generated.Agw.Jobs.AgwToolModule`。

在业务模块的 DI 入口注册生成清单和实例 Tool 类型：

```csharp
using Agw.Tools.Abstractions.Generated;
using Microsoft.Extensions.DependencyInjection;

services.AddSingleton<IAgwGeneratedToolModule>(
    Agw.Generated.My.Module.AgwToolModule.Instance);
services.AddScoped<ClockTools>();
services.AddSingleton<TimeProvider>(TimeProvider.System);
```

生成模块不持有 `ClockTools` 实例，也不创建 scope。实例方法执行时，`Agw.Tools` 会创建独立的异步 scope，再从该 scope 解析 `ClockTools`。因此，实例容器及其构造函数依赖必须在 DI 中登记。

静态 Tool 不需要注册所属类型；它通过 `[AgwToolService]` 参数使用当前调用 scope 中的服务。

### 4. 将生成 Tool 提供给 Skill

业务 Skill 实现 `IAgwToolSet<T>`，由 Generator 补齐 `ToolTypes`：

```csharp
public sealed partial class ClockSkillRegistration
    : IAgentSkillRegistration,
        IAgwToolSet<ClockTools>
{
    // Id、Name、Description 和 Create(...) 省略
}
```

注册 `IAgwGeneratedToolModule` 只让运行时能够按类型找到生成声明，不会把模块内所有 Tool 自动加入全局目录。Skill Tool 只有在相应 Skill 绑定到 Agent 或 Project 后才参与能力组合。

### 5. 将生成 Tool 加入全局目录

确实需要全局暴露时，在 Host 组合阶段显式选择类型：

```csharp
services.AddToolCatalogTypes(typeof(ClockTools));
```

全局 Tool 仍需遵守 `Agw.Tools` 的持久化 `ToolDefinition`、`ToolBlockDefinition`、JSON 多态登记及覆盖校验规则。仅添加 Analyzer 引用或注册 `IAgwGeneratedToolModule` 不会绕过这些规则。

## Attribute 参考

所有 Attribute 位于 `Agw.Tools.Abstractions.Attributes` 命名空间。

| Attribute                  | 目标     | 用途                                                     |
| -------------------------- | -------- | -------------------------------------------------------- |
| `AgwTool`                  | 方法     | 暴露单个方法，或覆盖容器默认值                           |
| `AgwToolContainer`         | 类       | 批量暴露该类直接声明的 public 普通方法，并提供默认元数据 |
| `AgwToolIgnore`            | 方法     | 完全禁止为该方法生成 Tool                                |
| `AgwToolService`           | 参数     | 从当前 Tool 调用的 DI scope 解析参数                     |
| `ExcludeToolFromList`      | 类、方法 | 保留 Tool 能力，但从 Tool 列表接口隐藏                   |
| `AgwToolParameterSchema`   | 参数     | 补充该参数的 JSON Schema 约束                            |
| `AgwToolRequiresWorkspace` | 类       | 在生成元数据中声明 Workspace 约束                        |

### `AgwToolAttribute`

`AgwTool` 支持三种构造方式：

```csharp
[AgwTool]
[AgwTool(AgwToolPermission.ReadOnly)]
[AgwTool("explicit_name", AgwToolPermission.Write)]
```

可配置属性如下：

| 属性 | 默认值或来源 | 说明 |
| --- | --- | --- |
| `Name` | 方法名 | 默认保留大小写，并移除末尾一个精确匹配的 `Async` |
| `Category` | 容器 `DefaultCategory`，再回退到 `"General"` | Tool 分类 |
| `RequiredPermission` | 方法构造参数或容器权限 | 必须显式声明或从容器继承 |
| `AllowInPlanMode` | 容器值，再回退到 `false` | 是否允许在 Plan Mode 中使用 |
| `TimeoutMs` | `5000` | 执行超时毫秒数；`0` 表示不设置超时 |
| `ReturnExceptionsAsResults` | `true` | 兼容性属性；当前 Agent 执行管线始终把 Tool 异常作为结果返回 |

显式名称保持原样，不执行 `Async` 处理：

```text
GetJobAsync       -> GetJob
getJobAsync       -> getJob
GetJob            -> GetJob
显式 "get_async"  -> get_async
```

Tool 名称按不区分大小写的方式检查冲突。生成名称为空或同一程序集内重复时，生成器报告编译错误。

方法级设置优先于容器级设置。生成器根据 Attribute 构造参数和显式命名参数判断是否配置过，因此 `false`、`AgwToolPermission.None` 和 `0` 都是有效的显式值，不会被当成“未配置”。

### `AgwToolContainerAttribute`

容器构造函数要求声明默认权限：

```csharp
[AgwToolContainer(
    AgwToolPermission.ReadOnly,
    DefaultCategory = "Projects",
    AllowInPlanMode = true)]
internal sealed class ProjectTools
{
    // public 普通方法默认生成
}
```

发现规则如下：

- 只处理类型直接声明的 public 普通方法；不处理继承方法、构造函数、属性访问器或事件访问器。
- 静态类的方法会生成静态直接调用；实例方法会从调用 scope 解析所属类型后直接调用。
- 没有 `AgwToolContainer` 的类型，只处理显式标记 `[AgwTool]` 的方法。
- 容器发现和方法级 `[AgwTool]` 同时命中时只生成一次。
- `[AgwToolIgnore]` 在签名和 Schema 校验之前排除方法。
- 标记 `[Obsolete]` 的方法或所属类型不会生成可调用 Tool，其名称会进入模块的 `ObsoleteToolNames`。

普通非静态类也可以包含显式公开的静态 Tool；生成器按照方法本身是否为静态方法选择调用方式。

### 描述和显示名称

方法描述的优先级为：

1. 方法上的 `System.ComponentModel.DescriptionAttribute`；
2. 方法 XMLDoc 的 `<summary>`；
3. 空字符串。

参数描述先读取参数上的 `DescriptionAttribute`，再读取方法 XMLDoc 中对应的 `<param>`。ToolBlock 的描述由用户定义的 `IAgwToolBlockDeclaration.Description` 提供，不会复制给成员方法。

可以使用 `DisplayNameAttribute` 覆盖 Tool 的显示名称；未指定时，显示名称等于最终 Tool 名称。

```csharp
/// <summary>
/// 查询指定项目。
/// </summary>
/// <param name="projectId">项目 ID。</param>
[DisplayName("查询项目")]
public Task<ProjectResponse> GetProjectAsync(
    Guid projectId,
    CancellationToken cancellationToken)
{
    throw new NotImplementedException();
}
```

XMLDoc 在编译期通过 Roslyn 读取并嵌入生成代码，运行时不需要查找 XML 文档文件。

### `AgwToolIgnoreAttribute`

`AgwToolIgnore` 表示该方法不是 Tool：

```csharp
[AgwToolIgnore]
public string NormalizeName(string name)
{
    return name.Trim();
}
```

即使方法同时被容器批量发现，或显式带有 `[AgwTool]`，`AgwToolIgnore` 仍会阻止生成：

- Tool 描述和调用委托；
- 输入及返回值 Schema；
- ToolBlock 成员关系；
- 模块清单中的 Tool 记录。

该方法仍可作为普通 C# 方法调用。

### `ExcludeToolFromListAttribute`

`ExcludeToolFromList` 与 `AgwToolIgnore` 的含义不同。它会生成并保留 Tool，只控制列表投影：

| 行为 | `AgwToolIgnore` | `ExcludeToolFromList` |
| --- | --- | --- |
| 生成 Tool 和 Schema | 否 | 是 |
| 进入 ToolBlock | 否 | 是 |
| 可通过生成入口调用 | 否 | 是 |
| `GET /api/tools` | 不存在 | 隐藏 |
| `GET /api/tools/by-category` | 不存在 | 隐藏，并移除空分类 |
| `GET /api/tools/{name}` | 不存在 | 保留注册查询行为 |

类级标记会应用到该容器生成的所有 Tool。ToolBlock 是否隐藏由其 `IAgwToolBlockDeclaration.ExcludeFromList` 独立决定；成员上的列表隐藏标记不会删减 Block 成员集合。

### partial ToolBlock 与类型关联

ToolBlock 由开发者编写 `partial class` 声明，使用 `IAgwToolSet<TToolContainer>` 建立类型安全的成员关系。Generator 会生成另一个 partial 部分，补齐 `ToolTypes` 和 Descriptor 工厂：

```csharp
[AgwToolContainer(
    AgwToolPermission.ReadOnly,
    DefaultCategory = "Time",
    AllowInPlanMode = true)]
internal sealed class ClockTools
{
    private readonly TimeProvider _timeProvider;

    public ClockTools(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public DateTimeOffset GetUtcNow()
    {
        return _timeProvider.GetUtcNow();
    }

    [AgwTool(Name = "unix_time_seconds")]
    public long GetUnixTimeSeconds()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeSeconds();
    }
}

internal sealed partial class ClockToolBlock
    : IAgwToolBlockDeclaration,
        IAgwToolSet<ClockTools>
{
    public string Name => "clock";

    public string DisplayName => "Clock";

    public string Description => "提供当前时间及时间戳。";
}
```

生成关系为：

```text
ToolBlock: clock
|-- GetUtcNow
`-- unix_time_seconds
```

`IAgwToolSet<ClockTools>` 只表达成员关系，Block 的 `Name` 只承担运行时稳定标识职责。Generator 不需要执行或计算 `Name`，也没有需要保持一致的重复字符串。

一个 Block 可以组合多个容器：

```csharp
internal sealed partial class ProjectToolBlock
    : IAgwToolBlockDeclaration,
        IAgwToolSet<ProjectQueryTools>,
        IAgwToolSet<ProjectCommandTools>
{
    public string Name => "project";

    public string DisplayName => "Project";
}
```

`DisplayName` 默认等于 `Name`，`Description` 默认为空，`Scopes` 默认为 `Agent | Project`，`ExcludeFromList` 默认为 `false`。ToolBlock 声明必须是顶层、具体、非泛型的 public 或 internal `partial class`，并提供 Generator 能访问的无参数构造函数。Generator 保留 `ToolTypes` 和 `CreateDescriptor` 成员名。

生成模块会把该类型实例加入 `ToolBlockRegistrations`，业务模块无需再单独注册 ToolBlock。运行时调用生成的 `CreateDescriptor`，按 `ToolTypes` 从 `IAgwGeneratedToolLookup` 读取成员描述，不扫描方法，也不计算 `Name` 来建立关联。

固定 Block 的成员在编译期确定。需要按会话动态产生 Tool、维护 Provider 状态或执行动态成员校验时，应继续使用 `Agw.Tools` 的手写 `IToolBlock` 路径。

### `AgwToolServiceAttribute`、`FromServicesAttribute` 与依赖注入

构造函数注入适合实例容器：

```csharp
internal sealed class ProjectTools
{
    private readonly ProjectAppService _service;

    public ProjectTools(ProjectAppService service)
    {
        _service = service;
    }

    [AgwTool(AgwToolPermission.ReadOnly)]
    public Task<ProjectResponse> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        return _service.GetAsync(projectId, cancellationToken);
    }
}
```

静态方法或单个方法级依赖使用 `[AgwToolService]`：

```csharp
[AgwTool(AgwToolPermission.ReadOnly)]
public static DateTimeOffset GetUtcNow(
    [AgwToolService] TimeProvider timeProvider)
{
    return timeProvider.GetUtcNow();
}
```

已经依赖 ASP.NET Core MVC 的模块也可以使用兼容的 `[FromServices]`：

```csharp
using Microsoft.AspNetCore.Mvc;

[AgwTool(AgwToolPermission.ReadOnly)]
public static DateTimeOffset GetUtcNow(
    [FromServices] TimeProvider timeProvider)
{
    return timeProvider.GetUtcNow();
}
```

两种 Attribute 的生成结果相同。服务参数不会进入 JSON Schema，也不能由模型提供；生成代码调用 `IAgwToolBindingContext.GetRequiredService<T>()`，从本次调用的 scope 解析服务。Generator 只按 `Microsoft.AspNetCore.Mvc.FromServicesAttribute` 的元数据名称识别，因此 `Agw.Tools.Abstractions` 和 `Agw.Tools.Generators` 不增加 ASP.NET Core 依赖。

同一参数不能同时标记 `[AgwToolService]` 和 `[FromServices]`，否则产生 `AGWTOOL002`。服务身份必须显式声明，Generator 不会根据 DI 中是否注册了参数类型进行猜测。通用业务模块优先使用 `[AgwToolService]`；已经具有 MVC 依赖的 Web 模块可以使用 `[FromServices]`。

`CancellationToken` 同样不会进入 Schema，运行时会把当前调用的取消令牌直接传入方法。

每次调用的生命周期为：

```text
权限、Plan Mode 和审批检查
              |
              v
创建独立 AsyncServiceScope
              |
              v
初始化可信 Project 调用上下文
              |
              v
解析容器和服务参数
              |
              v
绑定模型参数并直接调用方法
              |
              v
等待完成、序列化结果、释放 scope
```

并发调用各自使用独立 scope。`IAgwToolInvocationContext.ProjectId` 由运行时初始化，模型参数无法替换它。需要当前 Project 的 Tool 可注入该接口：

```csharp
public Task<Result> GetCurrentProjectDataAsync(
    [AgwToolService] IAgwToolInvocationContext invocationContext,
    CancellationToken cancellationToken)
{
    return LoadAsync(invocationContext.ProjectId, cancellationToken);
}
```

权限判断和审批发生在创建 Tool scope 之前。服务缺失、参数绑定失败和业务异常由现有 Tool 错误边界处理；scope 在成功、异常和取消路径都会释放。

### `AgwToolParameterSchemaAttribute`

生成器会从参数类型生成基础 Schema，`AgwToolParameterSchema` 用于增加静态约束：

```csharp
[AgwTool(AgwToolPermission.Write)]
public Task<CreateUserResponse> CreateUserAsync(
    [Description("用户显示名称")]
    [AgwToolParameterSchema(MinLength = 1, MaxLength = 80)]
    string name,
    [AgwToolParameterSchema(
        Type = "string",
        Format = "email",
        Pattern = "^[^@]+@[^@]+$")]
    string email,
    [AgwToolParameterSchema(Minimum = 0, Maximum = 10)]
    int retryCount = 3,
    CancellationToken cancellationToken = default)
{
    throw new NotImplementedException();
}
```

支持的补充项包括 `Type`、`Format`、逗号分隔的 `EnumValues`、`Minimum`、`Maximum`、`MinLength`、`MaxLength` 和 `Pattern`。只有显式写入的命名参数才会加入 Schema，因此 `Minimum = 0` 是有效约束。

补充 Schema 必须与真实参数的序列化和绑定方式一致。它只描述和约束模型输入，不负责替代 Application 层的业务校验。

### `AgwToolRequiresWorkspaceAttribute`

在容器上标记 Workspace 要求：

```csharp
[AgwToolRequiresWorkspace]
[AgwToolContainer(AgwToolPermission.ReadOnly)]
internal sealed class WorkspaceTools
{
    public Task<string[]> ListFilesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Array.Empty<string>());
    }
}
```

生成器将 `RequiresWorkspace = true` 写入每个 Tool 的描述符。Workspace 的可信解析和实际约束由 `Agw.Tools` 运行时完成。

## 元数据继承规则

最终元数据按下表解析：

| 字段 | 解析顺序 |
| --- | --- |
| Name | 方法显式名称；否则方法名移除末尾一个精确 `Async` |
| DisplayName | 方法 `DisplayNameAttribute`；否则最终 Name |
| Description | 方法 `DescriptionAttribute`；否则 XMLDoc `<summary>` |
| Category | 方法显式值；容器 `DefaultCategory`；`"General"` |
| RequiredPermission | 方法构造参数；容器构造参数；均没有则编译错误 |
| AllowInPlanMode | 方法显式值；容器值；`false` |
| TimeoutMs | 方法显式值；`5000` |
| ExcludeFromList | 方法或类型带 `ExcludeToolFromList` |
| RequiresWorkspace | 类型带 `AgwToolRequiresWorkspace` |

`RequiresConfirmation` 不由 Attribute 固定写入；运行时根据 `RequiredPermission` 和当前权限策略推导。

## JSON Schema 和参数绑定

输入 Schema 与调用参数绑定来自同一份编译期模型，避免“Schema 接受但调用器无法读取”的双轨实现。当前支持：

- `string`、`char`、`bool` 和常规整数、浮点数类型；
- `Guid`，生成为 `string/uuid`；
- `DateTimeOffset`，生成为 `string/date-time`；
- 普通枚举，生成为成员名称字符串枚举；
- 可空值类型和带可空标注的引用类型；
- 数组、`IEnumerable<T>`、`IReadOnlyList<T>`、`IList<T>`、`List<T>`；
- 字符串键的 `IDictionary<TKey,TValue>`、`IReadOnlyDictionary<TKey,TValue>`、`Dictionary<TKey,TValue>`；
- 由 public 可读实例属性组成的普通 class、struct、DTO 和 record；
- 属性上的 `JsonPropertyNameAttribute`、`JsonIgnoreAttribute` 和 `DescriptionAttribute`。

参数是否必填与是否允许 `null` 是两个维度：

- 没有显式默认值的业务参数进入输入 Schema 的 `required`；
- 有默认值的参数使用生成的 `GetOptionalArgument<T>` 绑定，并将默认值写入 Schema；
- 可空标注决定类型 Schema 是否允许 `null`；
- `[AgwToolService]` 参数和 `CancellationToken` 不进入输入 Schema。

DTO 属性是否进入 `required` 由 C# `required` 修饰符和可空标注决定。`JsonIgnoreAttribute` 标记的属性不进入 Schema，`JsonPropertyNameAttribute` 决定 Schema 中的属性名。

实际输入读取和结果序列化继续使用 `System.Text.Json` 与 `AIJsonUtilities.DefaultOptions`。返回类型会生成独立的 `ReturnJsonSchema`；`void`、`Task` 和 `ValueTask` 没有返回 Schema。支持同步返回、`void`、`Task`、`Task<T>`、`ValueTask` 和 `ValueTask<T>`。

生成器会拒绝无法静态安全描述或直接调用的声明，包括：

- 泛型 Tool 方法或泛型容器类型；
- `ref`、`in`、`out` 参数和 by-ref 返回；
- `async void`；
- 指针、`dynamic`、裸 `object` 和不支持的类型图；
- DTO 或属性上的自定义 `JsonConverterAttribute`；
- `JsonPolymorphicAttribute`、`JsonExtensionDataAttribute`；
- 循环引用的 DTO 类型图；
- 非字符串键字典。

遇到这些场景时，应改为可静态描述的 DTO，或使用现有的手写 `IAgwTool`、`IProjectScopedAgwTool`、`IContextualTool` 或 `IToolBlock` 接入路径。

## `Agw.Tools.Generators` 的工作方式

`AgwToolGenerator` 实现 `IIncrementalGenerator`，使用 `ForAttributeWithMetadataName` 定位 `AgwToolContainerAttribute` 类型和 `AgwToolAttribute` 方法，并通过带 base list 的类型语法定位 `IAgwToolSet<T>`。它只对相关语法和语义输入建立生成模型。

每个消费程序集生成一个 `AgwToolModule.g.cs`，其中包含：

| 产物 | 内容 |
| --- | --- |
| Tool 描述符 | 名称、显示名称、描述、分类、权限、Plan Mode、隐藏和 Workspace 标记等 |
| Schema 常量 | 输入 Schema，以及有返回值时的返回 Schema |
| 参数描述符 | 参数类型、描述、可选性、默认值和补充 Schema 信息 |
| 调用委托 | 读取类型化参数、解析服务、解析实例并直接调用目标方法 |
| ToolSet partial | 为任意 `IAgwToolSet<T>` 消费类型生成 `ToolTypes`；为 ToolBlock 额外生成 Descriptor 工厂 |
| ToolBlock registration | 用户定义的 partial ToolBlock 类型，由模块创建并交给运行时 |
| Obsolete 名称 | 被 `[Obsolete]` 排除的 Tool 名称 |

生成清单实现 `IAgwGeneratedToolModule`：

```csharp
public interface IAgwGeneratedToolModule
{
    IReadOnlyList<AgwGeneratedToolDescriptor> Tools { get; }

    IReadOnlyList<IAgwGeneratedToolBlockRegistration> ToolBlockRegistrations { get; }

    IReadOnlyList<string> ObsoleteToolNames { get; }
}
```

Tool 容器不需要声明为 `partial`。任何实现 `IAgwToolSet<T>` 的消费类型都必须是 partial class，因为 Generator 会向同一个类型补充 `ToolTypes`；如果它同时实现 `IAgwToolBlockDeclaration`，Generator 再补充 Descriptor 工厂。Generator 不识别或依赖 `IAgentSkillRegistration`。生成代码位于同一程序集，因此可以引用 internal 容器；公开消费类型使用泛型 marker 时，其 Tool 容器也必须满足 C# 的可访问性规则。

### 诊断

生成器使用两个编译错误编号：

| 编号 | 含义 | 常见原因 |
| --- | --- | --- |
| `AGWTOOL001` | 不支持的 Tool 签名或类型 | 泛型、ref/out、async void、不支持的 DTO 或 JSON converter |
| `AGWTOOL002` | Tool 或 ToolSet 声明无效 | 权限缺失、名称重复、ToolSet owner 未声明 partial、类型结构不支持或占用生成成员名 |

这是严格模式：出现诊断时必须修正声明或改用手写 Tool，不存在运行时反射回退。

### 查看生成代码

正常情况下不要提交 `.g.cs`。调试生成结果时，可以临时将编译器输出写入 `obj`：

```bash
dotnet build src/server/My.Module/My.Module.csproj \
  -p:EmitCompilerGeneratedFiles=true \
  -p:CompilerGeneratedFilesOutputPath=obj/Generated
```

重点检查生成的 `AgwToolModule.g.cs` 和 `<类型>.AgwToolSet.g.cs`：

- `Tools` 中是否存在目标方法；
- `JsonSchema` 和 `ReturnJsonSchema` 是否符合方法签名；
- `[AgwToolService]` 与 `CancellationToken` 是否未出现在输入 Schema；
- 实例方法是否通过 `context.GetRequiredService<ContainerType>()` 解析容器；
- `InvokeAsync` 是否直接调用目标方法；
- Skill 的 `ToolTypes` 和 ToolBlock 的 `CreateDescriptor` 是否使用预期容器类型。

## 生成清单与运行时的边界

`Agw.Tools.Abstractions` 中的生成契约只携带声明和调用桥接信息：

- `IAgwGeneratedToolModule` 是程序集级清单；
- `AgwGeneratedToolDescriptor` 是单个 Tool 的静态描述；
- `IAgwToolSet<T>` 用泛型参数表达 Skill 或 ToolBlock 与 Tool 容器的关系；
- `IAgwGeneratedToolBlockRegistration` 是 Generator 为用户 partial ToolBlock 补齐的运行时注册契约；
- `AgwGeneratedToolBlockDescriptor` 是 registration 在运行时从生成 Tool 目录创建的固定成员描述；
- `IAgwToolBindingContext` 向生成委托提供参数、服务和可信 Project ID；
- `IAgwToolInvocationContext` 向业务 Tool 暴露只读 Project ID；
- `IAgwToolInvocationContextInitializer` 由运行时实现，用于在 scope 内初始化可信上下文。

业务代码不应自行实现初始化器或用模型参数构造 Project 上下文。权限、审批、超时、错误映射、Registry 投影和 scope 管理由 `Agw.Tools` 负责。

## 与手写 Tool 的关系

Attribute Tool 适合签名能够静态描述、无需动态生成成员的普通方法。现有手写接口继续保留：

| 接入方式 | 适用情况 |
| --- | --- |
| `IAgwTool` | 无 Project 绑定的独立手写 Tool |
| `IProjectScopedAgwTool` | 创建时需要显式 Project ID 的独立手写 Tool |
| `IContextualTool` | 依赖动态上下文或运行时组合的 Tool |
| `IToolBlock` | 动态成员、Provider 状态或会话级行为的 ToolBlock |

不要为了使用生成器强行把动态 Schema、多态输入或复杂运行时组合压进 Attribute 方法。此类场景保留手写实现更清晰。

## 接入检查清单

新增 Attribute Tool 时依次确认：

1. 业务项目正常引用 `Agw.Tools.Abstractions`，并以 Analyzer 方式引用 `Agw.Tools.Generators`。
2. 每个 Tool 在方法或容器上明确声明 `AgwToolPermission`。
3. public 方法签名只使用生成器支持的输入、返回和 JSON 契约。
4. 实例容器和构造函数依赖已注册到 DI；方法级服务参数带 `[AgwToolService]` 或 `[FromServices]`，但不能同时使用。
5. Tool 名称在程序集和最终能力组合中唯一；默认名称是否应移除 `Async` 已核对。
6. 固定 ToolBlock 使用 `partial class` 和 `IAgwToolSet<T>` 关联成员，并保留可访问的无参数构造函数。
7. 业务模块已注册生成的 `IAgwGeneratedToolModule`。
8. Skill 专属 Tool 使用 `IAgwToolSet<T>` 声明；全局 Tool 已通过 `AddToolCatalogTypes` 显式选择。
9. 需要隐藏列表条目时使用 `ExcludeToolFromList`；需要完全禁止生成时使用 `AgwToolIgnore`。
10. 构建消费项目并处理所有 `AGWTOOL001`、`AGWTOOL002`，再运行对应 Tools、Skills 和业务模块测试。
