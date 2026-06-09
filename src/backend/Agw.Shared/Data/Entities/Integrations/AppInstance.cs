using System.ComponentModel.DataAnnotations.Schema;

namespace Agw.Shared.Data.Entities.Integrations;

[Table("app_instance")]
public class AppInstance : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }

    /// <summary>
    /// AppDefinition.Name, used to identify the app type and link to the app definitions
    /// </summary>
    public string AppName { get; set; } = string.Empty;

    public bool UsePkce { get; set; } = true;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public virtual OAuthAuthorizationToken? AuthorizationToken { get; set; }
}
