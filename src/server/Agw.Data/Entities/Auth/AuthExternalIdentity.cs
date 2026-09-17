using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Auth;

[Table("auth_external_identity")]
[EntityTypeConfiguration(typeof(AuthExternalIdentityConfiguration))]
public sealed class AuthExternalIdentity : BaseEntity
{
    public long UserId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
}
