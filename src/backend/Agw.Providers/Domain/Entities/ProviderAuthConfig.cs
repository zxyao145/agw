using Agw.Shared;
using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Providers.Domain.Entities;


[Table("provider_auth_config")]
public class ProviderAuthConfig : BaseEntity
{
    public Guid Id { get; set; }
    public Guid ProviderId { get; set; }
    public ProviderAuthType AuthType { get; set; } = ProviderAuthType.ApiKey;
    public string? ApiKey { get; set; }
    public string? EnvName { get; set; }
    public bool Enable { get; set; } = true;

    public Provider? Provider { get; set; }
}
