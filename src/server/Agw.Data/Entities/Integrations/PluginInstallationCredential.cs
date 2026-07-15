using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Integrations;

/// <summary>
/// 插件的受保护凭据。
/// Represents a protected credential owned by a plugin installation.
/// </summary>
[Table("plugin_installation_credential")]
[EntityTypeConfiguration(typeof(PluginInstallationCredentialConfiguration))]
public class PluginInstallationCredential : BaseEntity
{
    public Guid Id { get; set; }
    public Guid PluginInstallationId { get; set; }
    public string Slot { get; set; } = string.Empty;

    /// <summary>
    /// 加密存储 client secret 等信息
    /// </summary>
    [JsonIgnore]
    public string ProtectedValue { get; set; } = string.Empty;

    public int FormatVersion { get; set; } = 1;

    [JsonIgnore]
    public PluginInstallation PluginInstallation { get; set; } = null!;
}
