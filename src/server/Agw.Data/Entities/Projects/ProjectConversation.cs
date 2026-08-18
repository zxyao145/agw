using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_conversation")]
[EntityTypeConfiguration(typeof(ProjectConversationConfiguration))]
public class ProjectConversation : BaseEntity
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid? JobId { get; set; }

    public string ContextId { get; set; } = string.Empty;

    public string Title { get; set; } = "Untitled";

    [JsonIgnore]
    public virtual Project? Project { get; set; }

    [JsonIgnore]
    public virtual ICollection<ProjectConversationChatHistory> ChatHistories { get; set; } =
        new List<ProjectConversationChatHistory>();
}
