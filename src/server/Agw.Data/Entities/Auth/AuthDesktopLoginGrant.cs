using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Auth;

[Table("auth_desktop_login_grant")]
[EntityTypeConfiguration(typeof(AuthDesktopLoginGrantConfiguration))]
public sealed class AuthDesktopLoginGrant : BaseEntity
{
    public string CodeHash { get; set; } = string.Empty;
    public long UserId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string CodeChallenge { get; set; } = string.Empty;
    public int SessionVersion { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
