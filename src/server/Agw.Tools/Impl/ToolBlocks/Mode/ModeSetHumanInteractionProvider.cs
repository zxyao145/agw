using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Tools.HumanInteraction;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.ToolBlocks.Mode;

internal sealed class ModeSetHumanInteractionProvider : AIContextProvider
{
    internal const string ModeSetToolName = "mode_set";
    internal const string InteractionKind = "mode-change";

    private static readonly ModeSetInteractionProtocol _protocol = new();

    public override IReadOnlyList<string> StateKeys => [];

    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        if (context.AIContext.Tools != null)
        {
            // mode_set 由前置 AgentModeProvider 动态生成，在这里声明它需要用户确认。
            // 包装后先通过 HumanInteraction 获取输入，确认后才调用原函数；mode_get 保持普通读取。
            // 这是 UserInput 语义，FullAccess 不会代替用户确认；Durable 适配在最终工具集合上完成。
            context.AIContext.Tools = context
                .AIContext.Tools.Select(static tool =>
                    tool is AIFunction function
                    && string.Equals(function.Name, ModeSetToolName, StringComparison.OrdinalIgnoreCase)
                        ? new HumanInteractionRequiredAIFunction(function, _protocol)
                        : tool
                )
                .ToArray();
        }

        return ValueTask.FromResult(context.AIContext);
    }

    private sealed class ModeSetInteractionProtocol : IHumanInteractionProtocol
    {
        public UserInputRequest CreateRequest(AIFunctionArguments arguments)
        {
            var mode = ReadMode(arguments);
            return new UserInputRequest(
                InteractionKind,
                $"The agent wants to switch to {ToDisplayName(mode)} mode.",
                JsonSerializer.SerializeToElement(new { mode })
            );
        }

        public AIFunctionArguments BindResponse(AIFunctionArguments arguments, UserInputResponse response)
        {
            if (
                response.ResponseData is not { ValueKind: JsonValueKind.Object } responseData
                || !responseData.TryGetProperty("confirmed", out var confirmed)
                || confirmed.ValueKind != JsonValueKind.True
            )
            {
                throw new AgwException(ErrorCodes.InvalidParam, "Mode change confirmation is required.");
            }

            return arguments;
        }

        public object CreateCancelledResult(AIFunctionArguments arguments, UserInputResponse response)
        {
            var mode = ReadMode(arguments);
            return $"Mode change to \"{mode}\" was cancelled by the user.";
        }

        private static string ReadMode(AIFunctionArguments arguments)
        {
            if (!arguments.TryGetValue("mode", out var value))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "mode_set.mode is required.");
            }

            var mode = value switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            };
            if (mode is not ("plan" or "execute"))
            {
                throw new AgwException(ErrorCodes.InvalidParam, "mode_set.mode must be either 'plan' or 'execute'.");
            }

            return mode;
        }

        private static string ToDisplayName(string mode) =>
            string.Equals(mode, "plan", StringComparison.Ordinal) ? "Plan" : "Execute";
    }
}
