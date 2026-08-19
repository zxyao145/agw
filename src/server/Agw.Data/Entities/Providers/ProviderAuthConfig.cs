using System.ComponentModel.DataAnnotations.Schema;
using Agw.Shared.Data.Encryption;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Providers;

[Table("provider_auth_config")]
[EntityTypeConfiguration(typeof(ProviderAuthConfigConfiguration))]
public class ProviderAuthConfig : BaseEntity
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public ProviderAuthType AuthType { get; set; } = ProviderAuthType.ApiKey;

    [Encrypted]
    public string? ApiKey { get; set; }
    public string? EnvName { get; set; }
    public bool Enable { get; set; } = true;

    public Provider? Provider { get; set; }
}
