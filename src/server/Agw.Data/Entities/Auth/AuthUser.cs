using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Auth;

[Table("auth_user")]
[EntityTypeConfiguration(typeof(AuthUserConfiguration))]
public sealed class AuthUser : BaseEntity
{
    public long Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public int SessionVersion { get; set; } = 1;
}
