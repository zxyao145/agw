namespace Agw.Agents.Contracts.Messages;

/// <summary>
/// Content type names matching Microsoft.Extensions.AI content types.
/// </summary>
public static class AiMessageContentType
{
    public const string DataContent = nameof(DataContent);
    public const string ErrorContent = nameof(ErrorContent);
    public const string FunctionCallContent = nameof(FunctionCallContent);
    public const string FunctionResultContent = nameof(FunctionResultContent);
    public const string HostedFileContent = nameof(HostedFileContent);
    public const string HostedVectorStoreContent = nameof(HostedVectorStoreContent);
    public const string TextContent = nameof(TextContent);
    public const string TextReasoningContent = nameof(TextReasoningContent);
    public const string UriContent = nameof(UriContent);
    public const string UsageContent = nameof(UsageContent);
}
