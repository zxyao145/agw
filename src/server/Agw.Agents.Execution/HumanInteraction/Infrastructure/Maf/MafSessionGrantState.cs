using System.Text.Json.Nodes;

namespace Agw.Agents.Execution.HumanInteraction.Infrastructure.Maf;

public sealed class MafSessionGrantState
{
    public Guid? PermissionScopeId { get; set; }
    public long PermissionVersion { get; set; }
    public PermissionMode? PermissionMode { get; set; }
    public List<MafToolGrant> Grants { get; set; } = [];
}

public sealed class MafToolGrant
{
    public required string ToolName { get; set; }
    public ApprovalScope Scope { get; set; }
    public JsonNode? Arguments { get; set; }
}
