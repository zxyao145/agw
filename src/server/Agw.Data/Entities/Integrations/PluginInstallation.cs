using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Agw.Shared.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Shared.Data.Entities.Integrations;

/// <summary>
/// 表示当前用户的集成插件 setup。
/// Represents one user's setup for an installed integration plugin.
/// </summary>
[Table("plugin_installation")]
[EntityTypeConfiguration(typeof(PluginInstallationConfiguration))]
public class PluginInstallation : BaseEntity, IAggregateRoot
{
    public Guid Id { get; set; }
    public string PluginId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 存储 client id 等信息
    /// </summary>
    public string ConfigurationJson { get; set; } = "{}";

    [JsonIgnore]
    public ICollection<PluginInstallationCredential> Credentials { get; set; } =
        new List<PluginInstallationCredential>();
}
