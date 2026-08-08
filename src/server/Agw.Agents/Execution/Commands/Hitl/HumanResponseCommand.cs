using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

using Agw.Agents.Execution.Commands.Abstracts;

namespace Agw.Agents.Execution.Commands.Hitl;

public class HumanResponseCommand : AgentRunCommand
{
    private string _approvalScope = "once";

    [JsonConstructor]
    [SetsRequiredMembers]
    public HumanResponseCommand(
        string requestId,
        bool approved,
        string? responseText = null,
        string approvalScope = "once",
        JsonElement? responseData = null)
    {
        RequestId = requestId;
        Approved = approved;
        ResponseText = responseText;
        ApprovalScope = approvalScope;
        ResponseData = responseData;
    }

    public string RequestId { get; set; }

    public bool Approved { get; set; }

    public string? ResponseText { get; set; }

    public JsonElement? ResponseData { get; set; }

    public string ApprovalScope
    {
        get => _approvalScope;
        set => _approvalScope = string.IsNullOrWhiteSpace(value) ? "once" : value.Trim();
    }
}
