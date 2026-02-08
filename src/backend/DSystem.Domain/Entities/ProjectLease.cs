using DSystem.Shared;

namespace DSystem.Domain.Entities;

/// <summary>
/// Distributed lease for scheduling tasks within a project.
/// One row per project. Ownership is determined by <see cref="LockedBy"/> with an expiration time <see cref="LockedUntilUtc"/>.
/// </summary>
public class ProjectLease : BaseEntity
{
    public Guid ProjectId { get; set; }
    public string LockedBy { get; set; } = string.Empty;
    public DateTime LockedUntilUtc { get; set; }

    public Project? Project { get; set; }
}