using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Tools;

[Table("user_memory")]
[EntityTypeConfiguration(typeof(UserMemoryConfiguration))]
public sealed class UserMemory : BaseEntity
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Encrypted]
    public string Content { get; set; } = string.Empty;
}
