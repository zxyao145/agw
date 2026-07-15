using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Agw.Shared.Data.Encryption;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Integrations;

/// <summary>
/// 插件凭据。
/// Represents a credential owned by a plugin installation.
/// </summary>
[Table("plugin_installation_credential")]
[EntityTypeConfiguration(typeof(PluginInstallationCredentialConfiguration))]
public class PluginInstallationCredential : BaseEntity
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public string Slot { get; set; } = string.Empty;

    /// <summary>
    /// Client secret 等凭据的明文语义值；持久化时由 DbContext 自动加密。
    /// </summary>
    [JsonIgnore]
    [Encrypted]
    public string Value { get; set; } = string.Empty;

    public int FormatVersion { get; set; } = 1;

    [JsonIgnore]
    public PluginInstallation PluginInstallation { get; set; } = null!;
}
