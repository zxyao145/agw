# Ollama Provider 支持方案

## 背景

Agw 当前支持 `OpenAIChatCompletions`、`OpenAIResponses` 和 `Anthropic`。本次增加 `ProviderType.OllamaNative = 3`，通过 `OllamaSharp` 提供的 `IChatClient` 接入现有 Provider 配置、模型发现和 Agent 执行流程。[Microsoft Agent Framework 的 Ollama C# 文档](https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/ollama)

## 设计目标

### 功能目标

1. Web、Desktop 的 Provider 表单提供 `OllamaNative` 选项，支持 Endpoint 和可选 API Key。
2. 获取 Ollama 已安装的模型，使用现有流程保存模型绑定。
3. System Agent 和对话摘要能够使用 Ollama；使用这些 Agent 的 Agentflow 节点及后台 Agent 通过现有创建流程获得支持。

### 技术目标

- 使用原生 Ollama API 和 SDK 的 MEAI 映射，复用现有 `EngineKind.Maf` 执行流程。
- `HttpClient` 通过 `IHttpClientFactory` 创建，Endpoint、认证和资源释放由客户端创建代码统一处理。
- 使用现有 Model 参数配置请求，延续现有用户隔离、取消、历史记录和工具执行机制。

## 数据库设计

- 在 `ProviderType` 末尾增加 `OllamaNative = 3`，现有枚举数值保持不变。
- 使用现有 `provider`、认证配置、模型及模型绑定数据结构。
- JSON 中使用字符串 `"OllamaNative"`。
- 无表结构、索引或字段变更，无需 EF migration。

## 详细设计

### 1. Provider 配置

- 在 `ProviderType` 增加 `OllamaNative = 3`，共享 `@agw/providers` 表单增加对应选项。重新生成 OpenAPI 客户端类型。
- 选择 `OllamaNative` 时，Name 和 Endpoint 分别提示 `ollama`、`http://localhost:11434`，保留用户已经填写的值。
- Web 和 Desktop 使用共享 Provider 表单。
- Endpoint 为 Ollama 服务基础地址。请求前验证其为绝对 HTTP(S) 地址，非法地址使用现有 `ErrorCodes.InvalidUrl`。
- `BaseAddress` 的路径以 `/` 结尾，保留路径前缀，例如 `http://host/ollama/`。SDK 的 `api/chat`、`api/tags` 相对路径基于该地址发送。[SDK 路径定义](https://github.com/awaescher/OllamaSharp/blob/135abcc3e31ac6a1ec535ff4c891e11ed1328df4/src/OllamaSharp/Constants/Endpoints.cs)
- Endpoint 是后端访问的地址。Split Hosts 中，Control Plane 和 Data Plane 均须能够访问它。
- 没有启用且非空的 API Key 时使用无认证连接；存在有效 API Key 时使用 `Authorization: Bearer`。运行时沿用现有密钥选择方式，模型发现使用界面选出的第一个有效密钥。[Ollama 认证文档](https://docs.ollama.com/api/authentication)

### 2. 模型发现与绑定

- 继续使用 `IProviderModelDiscoveryService`，HTTP/SDK 实现归入 Providers 的 Infrastructure。
- 为 `OllamaNative` 调用 `OllamaApiClient.ListLocalModelsAsync(cancellationToken)`，对应原生 `GET /api/tags`。[模型列表接口](https://docs.ollama.com/api/tags)
- 返回模型 `Name`，保留 `:tag` 等完整名称，沿用现有空名称过滤及去重行为。
- `ProviderModelDiscoveryRequest.ApiKey` 修改为 `string? ApiKey = null`，由各 Provider 分支处理认证要求。
- `provider-models-editor.tsx` 同时调整 `discoveryDisabled`、`mutationFn` 内的 API Key 检查和提示信息。`OllamaNative` 在 Endpoint 非空且没有进行中的请求时允许获取模型，无密钥时请求发送 `apiKey: null`。
- 发现错误使用现有 `ProviderModelDiscoveryFailed`，用户取消继续传播取消异常。
- 保存 Provider 时沿用现有模型创建、复用和绑定流程。用户通过 Models 页面配置 `MaxContextWindowTokens`、`MaxOutputTokens`，满足现有 `0 < MaxOutputTokens < MaxContextWindowTokens` 校验及运行环境要求。

### 3. Agent 执行与摘要

- 在中央 NuGet 版本管理中增加 `OllamaSharp 5.5.0`，由 `Agw.Providers` 和 `Agw.Agents.Execution` 引用。该版本提供 .NET 10 目标及 `Microsoft.Extensions.AI` 集成。[包信息](https://www.nuget.org/packages/OllamaSharp/5.5.0)
- 在 Execution 内提供 Agent 与摘要共用的客户端创建代码，通过 `IHttpClientFactory` 创建 `HttpClient`，配置 Endpoint、可选认证并构造 `OllamaApiClient(HttpClient, modelName)`。
- 创建代码负责释放传入 SDK 的 `HttpClient`，衔接 Agent 资源释放及摘要的 `using` 生命周期。
- 在 `AgentRuntimeFactory.CreateDefinitionAgentAsync` 和 `SummaryChatClientFactory.CreateAsync` 中增加 `ProviderType.OllamaNative` 分支。Agentflow 节点和后台 Agent 通过现有 Definition Agent 创建流程获得支持。
- 两个创建入口均允许 `OllamaNative` 使用空认证配置。
- Agent 使用 `AsAgwAgent`，沿用现有工具执行、审批、历史记录和执行事件处理；摘要继续使用 `IChatClient.GetResponseAsync`。
- 在客户端创建代码中通过 `ConfigureOptions` 和 `AddOllamaOption(OllamaOption.NumCtx, model.MaxContextWindowTokens)` 设置 `options.num_ctx`；调用方未指定输出上限时使用 `model.MaxOutputTokens`，由 SDK 映射为 `options.num_predict`。Ollama 的输出上限包含 thinking 和回答，模型参数同时供现有 Compaction 使用。
- 普通响应、流式响应和工具内容使用 SDK 的 MEAI 映射；请求取消和服务错误进入现有执行处理流程。

## 接口文档

### 基础信息

- 管理接口由 Control Plane 或 Standalone Host 提供，使用现有 Agw 服务地址和登录认证。
- 请求使用 JSON，响应继续采用 Bens.Results，由 `ApiResult.Ok(...)` 返回成功结果。
- `providerType` 为字符串枚举，新增值为 `"OllamaNative"`。
- Provider Endpoint 指向 Ollama 服务，Split Hosts 部署中必须同时允许 Control Plane 和 Data Plane 访问。
- 请求中的 `apiKey` 用于访问 Ollama 服务；Agw 管理接口的身份认证沿用现有机制。

### 接口列表

#### Provider 创建、编辑与读取

现有 `POST /api/providers`、`PUT /api/providers/{id}`、`GET /api/providers` 和 `GET /api/providers/{id}` 接受或返回 `providerType: "OllamaNative"`，保持现有路由和响应结构。

创建请求示例：

```json
{
  "name": "Local Ollama",
  "providerType": "OllamaNative",
  "description": null,
  "endpoint": "http://localhost:11434",
  "authConfigs": [],
  "modelNames": ["qwen3:4b"]
}
```

客户端通过现有 Models 页面和接口读取、编辑模型参数。

#### 模型发现

**方法与路径：** `POST /api/providers/discover-models`，扩展现有接口。

请求示例：

```json
{
  "providerType": "OllamaNative",
  "endpoint": "http://localhost:11434",
  "apiKey": null
}
```

成功响应继续使用 `ApiResult.Ok(...)`，其中 `data` 示例：

```json
{
  "modelNames": ["qwen3:4b"]
}
```

`apiKey` 允许省略或为 `null`；`OllamaNative` 分支将空白值视为没有配置密钥。已有 OpenAI 请求保持兼容。

## 测试计划

自动化测试使用现有测试项目和 xUnit v3。实际模型请求通过本地真实 Ollama 服务验收。

### 配置测试

- `CreateClient_EndpointWithPathPrefix_PreservesPrefix`（`Agw.Agents.Tests`）：使用根地址及带路径前缀的地址创建实际客户端，检查 SDK 元数据中的 Endpoint，确认前缀和结尾斜线正确。
- `CreateClient_InvalidEndpoint_ThrowsInvalidUrl`（`Agw.Agents.Tests`）：传入相对地址及非 HTTP(S) 地址；创建客户端；确认返回 `InvalidUrl`，请求未发出。该用例无需 Ollama 服务。

### 单元与组件测试

使用实际 DI、SQLite、Providers 和客户端实现。测试中间文件放在项目内已忽略的 `artifacts/ollama-tests/`。

- `CreateAndUpdateAsync_OllamaWithoutAuth_PreservesProviderAndOwnership`（`Agw.Projects.Tests`）：没有认证配置时创建、编辑并读取 Provider；确认类型以 `"OllamaNative"` 返回，模型绑定正确，其他用户无法读取或修改。
- `CreateAsync_OllamaConfiguration_ReturnsOwnedChatClient`（`Agw.Agents.Tests`）：从 Agent 和摘要创建入口取得实际客户端，覆盖空认证及有效认证配置；检查模型、Endpoint 元数据及资源释放。

### 本地真实服务验收

准备可访问的 Ollama 服务及已安装的文本模型。通过现有 Models 页面设置适合本机的参数，例如上下文 `8192`、输出上限 `2048`。以下为手工验收，使用真实后端执行并记录服务版本、模型名称和结果：

1. 分别在 Web、Desktop 创建无认证的 `OllamaNative` Provider，获取模型并保存绑定，确认列表与实际安装模型一致。
2. 创建使用该模型的 System Agent，执行普通和流式文本对话，确认非空响应、正常结束原因及历史保存。
3. 使用支持工具调用的模型完成一次实际计算工具调用，确认调用参数、工具结果和最终回答正确。
4. 执行对话摘要，并分别运行使用该 Agent 的 Agentflow 节点和后台 Agent，确认实际请求到达 Ollama。
5. 检查实际请求地址、可选认证头及 `num_ctx`、`num_predict` 与配置一致，取消请求及模型不存在时的错误按现有流程返回。

### 回归与验收

1. 运行后端构建及 `Agw.Projects.Tests`、`Agw.Agents.Tests`；依赖或模块位置调整后运行 `Agw.Architecture.Tests`。
2. 运行客户端类型检查、边界检查及 `@agw/providers`、`@agw/api` 的测试。
3. 上述检查通过后执行本地真实服务验收，检查现有三个 Provider 的创建、认证、模型发现和客户端构造行为。
4. 对修改文件执行格式检查。
