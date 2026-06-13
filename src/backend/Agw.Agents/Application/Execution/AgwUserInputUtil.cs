using Agw.Shared.AgwMsgVm;

namespace Agw.Agents.Application.Execution;

internal static class AgwUserInputUtil
{
    public static string ExtractAgentflowInputText(AgwUserInput input)
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
}
