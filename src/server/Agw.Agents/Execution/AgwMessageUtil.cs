using Agw.Agents.Execution.Runtimes;
using Agw.Shared.AgwMsgVm;

using ClaudeCodeSdk.MAF;

namespace Agw.Agents.Execution;

internal static class AgwMessageUtil
{
    #region runtimes

    public static string ExtractInputText(AgwUserInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return string.Join(
            Environment.NewLine,
            input.Contents
                .Select(ExtractContentText)
                .Where(static value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? ExtractContentText(AgwContent content)
    {
        return content switch
        {
            AgwTextContent text => text.Content,
            AgwTextReasoningContent textReasoning => textReasoning.Content,
            AgwErrorContent error => error.Content,
            AgwFunctionCallContent functionCall => functionCall.Content,
            AgwFunctionResultContent functionResult => functionResult.Content,
            AgwUriContent uri => uri.Uri.ToString(),
            _ => null
        };
    }

    #endregion

    #region agents

    /// <summary>
    /// 处理 result message
    /// </summary>
    /// <param name="session"></param>
    /// <param name="agwMessage"></param>
    /// <returns></returns>
    public static AgwMessage PostAgwMessage(AgentRuntime session, AgwMessage agwMessage)
    {
        if (IsResult(session, agwMessage))
        {
            agwMessage = agwMessage with
            {
                Author = Constants.DefaultAgentAuthor
            };
        }

        return agwMessage;
    }
    
    
    public static bool IsResult(AgentRuntime session, AgwMessage agwMessage)
    {
        if (session.Agent is ClaudeCodeAIAgent)
        {
            if (agwMessage.AdditionalProperties != null
                && agwMessage.AdditionalProperties.TryGetValue("type", out object? type))
            {
                string? typeValue = (string?)type;
                if (typeValue == "result")
                {
                    return true;
                }
            }
        }
        
        return false;
    }

    #endregion
}
