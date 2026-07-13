using System.ComponentModel.DataAnnotations.Schema;

using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Shared.Data.Entities.Tasks;

[Table("project_app_relation")]
public class ProjectAppRelation : IAggregateRoot
{
    public Guid ProjectId { get; set; }
    public Guid AppInstanceId { get; set; }

    public Project Project { get; set; } = null!;
    public AppInstance AppInstance { get; set; } = null!;
}
