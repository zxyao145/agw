# 新增输入协议

例如工具需要用户提供一个短标题，使用 `UserInputRequest` 描述提示和载荷，由协议解释答案。工具不创建 `InteractionId`，也不引用 execution、SignalR、数据库或 MAF approval 类型。

```csharp
using System.Text.Json;
using Agw.Agents.Contracts.Execution;
using Agw.Tools.HumanInteraction;
using Microsoft.Extensions.AI;

internal sealed class TitleInputProtocol : IHumanInteractionProtocol
{
    public UserInputRequest CreateRequest(AIFunctionArguments arguments) =>
        new("title", "请输入标题", JsonSerializer.SerializeToElement(new
        {
            suggestedTitle = arguments["suggestedTitle"],
        }));

    public AIFunctionArguments BindResponse(AIFunctionArguments arguments, UserInputResponse response)
    {
        var title = response.ResponseData?.GetProperty("title").GetString();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("标题不能为空。");
        }
        return new AIFunctionArguments(new Dictionary<string, object?>(arguments)
        {
            ["title"] = title.Trim(),
        }) { Services = arguments.Services };
    }

    public object CreateCancelledResult(AIFunctionArguments arguments, UserInputResponse response) =>
        "用户取消了标题输入。";
}

// 在本工具的物化位置装配，内部 action 只收到已校验的用户输入。
var function = AIFunctionFactory.Create(
    (string suggestedTitle, string title) => title,
    new AIFunctionFactoryOptions { Name = "choose_title" });
var tool = new HumanInteractionRequiredAIFunction(function, new TitleInputProtocol());
```

需要排除模型伪造答案或敏感参数时，协议同时指定 `UserInputRequest.Arguments`，作为跨分段恢复的安全调用参数。`ask_user_question` 就只保存问题和 metadata。载荷结构和答案校验由协议拥有；公共卡片不会暴露恢复用的 Arguments。

把包装后的工具放入静态工具集合，或在运行时 provider 中生成。Durable 的 `DeferredHumanInteractionProvider` 在所有工具生成 provider 之后运行，自动建立暂停边界。动态 `mode_set` 已通过相同入口接入，不需要 Runner 特判。

客户端在共享 `execution-core` / `chat-core` 上解析统一请求，在 Web/Desktop 和 Native 的输入面板中为新的 `inputKind` 提供展示与提交。新增交互形式需要新增 UI；现有形式的新工具可复用原面板。

```json
{
  "type": "interaction-request",
  "interaction": {
    "kind": "user-input",
    "interactionId": "server-assigned-id",
    "prompt": "请输入标题",
    "source": { "nodeId": "writer", "toolName": "choose_title", "callId": "call-1" },
    "inputKind": "title",
    "payload": { "suggestedTitle": "草稿" }
  }
}
```

上面的对象位于 `AgwMessage.additionalProperties`。用户提交：

```json
{
  "type": "HumanResponseCommand",
  "executionId": "019f05b6-2400-7000-8000-000000000001",
  "response": {
    "kind": "user-input",
    "interactionId": "server-assigned-id",
    "cancelled": false,
    "responseData": { "title": "最终标题" }
  }
}
```

取消时提交 `cancelled: true`，省略 `responseData`。普通工具提交 `kind: tool-approval`、`approved`、`scope`（`Once` / `AlwaysTool` / `AlwaysArguments`）；HumanGate 提交 `kind: workflow-gate`、`approved` 和可选 `responseText`。

扩展无需修改 `InteractionRules`、InProcess pending 管理、Durable Store / Runner、命令分派器或 `MafApprovalAdapter`。请用两条链路的契约测试证明：发布前已登记、载荷不混入模型答案、取消不执行内部 action、跨分段恢复保持身份。可直接参考 `MafInteractionIntegrationTests` 中的测试用 `sample` 协议。
