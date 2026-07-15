using System.ComponentModel.DataAnnotations.Schema;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Agents;

[Table("mcp_server")]
[EntityTypeConfiguration(typeof(McpServerConfiguration))]
public sealed class McpServer : BaseEntity, IAggregateRoot
{
    /// <summary>
    /// Unique identifier for the MCP server (e.g., "github-mcp").
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable display name (e.g., "GitHub MCP Server").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Description of the server's capabilities.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Transport type: "stdio", "http".
    /// </summary>
    public string TransportType { get; set; } = "stdio";

    /// <summary>
    /// Command to launch the MCP server process (stdio transport only).
    /// Examples: "dnx", "npx", "python"
    /// </summary>
    public string? Command { get; set; }

    /// <summary>
    /// Arguments for the command (stdio transport only).
    /// Example: ["-y", "@modelcontextprotocol/server-github"]
    /// </summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>
    /// Working directory for the stdio process.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Environment variables for the stdio process.
    /// Values may reference secrets using {{secret:key_name}} syntax.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    /// <summary>
    /// URL for HTTP/SSE transport.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// HTTP headers for HTTP/SSE transport (e.g., auth tokens).
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Whether this server is enabled for tool discovery and agent use.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public ICollection<AgentMcpServerRelation> AgentMcpToolServers { get; set; } = new List<AgentMcpServerRelation>();
}
