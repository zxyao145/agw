using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Agw.Shared.Data.Entities.Integrations;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_connection_relation")]
[EntityTypeConfiguration(typeof(ProjectConnectionRelationConfiguration))]
public class ProjectConnectionRelation : IAggregateRoot
{
    public Guid ProjectId { get; set; }
    public Guid ConnectionId { get; set; }
    [JsonIgnore]
    public Project Project { get; set; } = null!;
    [JsonIgnore]
    public Connection Connection { get; set; } = null!;
}
