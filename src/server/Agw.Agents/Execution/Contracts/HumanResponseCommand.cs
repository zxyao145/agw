using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Agw.Agents.Execution.Contracts;

public class HumanResponseCommand : AgentRunCommand
{
    [JsonConstructor]
    [SetsRequiredMembers]
    public HumanResponseCommand(
        string requestId,
        bool approved,
        string? responseText = null)
    {
        RequestId = requestId;
        Approved = approved;
        ResponseText = responseText;
    }

    public string RequestId { get; set; }

    public bool Approved { get; set; }

    public string? ResponseText { get; set; }
}
