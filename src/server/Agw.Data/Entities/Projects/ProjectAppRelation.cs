using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Entities.Integrations;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Projects;

[Table("project_app_relation")]
[EntityTypeConfiguration(typeof(ProjectAppRelationConfiguration))]
public class ProjectAppRelation : IAggregateRoot
{
    public Guid ProjectId { get; set; }
    public Guid AppInstanceId { get; set; }

    public Project Project { get; set; } = null!;
    public AppInstance AppInstance { get; set; } = null!;
}
