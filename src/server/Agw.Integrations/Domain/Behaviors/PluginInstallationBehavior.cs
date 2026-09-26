using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Domain.Behaviors;

public sealed class PluginInstallationBehavior
{
    private readonly PluginInstallation _installation;

    public PluginInstallationBehavior(PluginInstallation installation)
    {
        _installation = installation;
    }

    /// <summary>
    /// <para>写入或清除某个连接器与认证方案下安装字段对应的密钥槽位；未列出的字段保持原值。</para>
    /// <para>Sets or clears the secret slots of setup fields under one connector and auth scheme; unlisted fields keep their values.</para>
    /// </summary>
    public void ApplySecretUpdates(
        ResolvedIntegrationDefinition definition,
        IReadOnlyDictionary<string, string> secretsToSet,
        IReadOnlyCollection<string> clearedFieldIds,
        string actor,
        DateTimeOffset now
    )
    {
        foreach (var fieldId in clearedFieldIds)
        {
            var credential = FindCredential(GetSlot(definition, fieldId));
            if (credential != null)
            {
                _installation.Credentials.Remove(credential);
            }
        }

        foreach (var (fieldId, value) in secretsToSet)
        {
            var slot = GetSlot(definition, fieldId);
            var credential = FindCredential(slot);
            if (credential == null)
            {
                credential = new PluginInstallationCredential
                {
                    PluginInstallationId = _installation.Id,
                    PluginInstallation = _installation,
                    Slot = slot,
                    CreateBy = actor,
                    CreateTime = now,
                };
                _installation.Credentials.Add(credential);
            }
            else
            {
                credential.UpdateBy = actor;
                credential.UpdateTime = now;
            }

            credential.Value = value;
            credential.FormatVersion = 1;
        }
    }

    /// <summary>
    /// <para>当前连接器与认证方案要求的安装字段都已配置：密钥字段有对应槽位，其他字段有非空值。</para>
    /// <para>Every setup field required by the connector and auth scheme is configured: secret fields have a slot and other fields have a value.</para>
    /// </summary>
    /// <param name="definition">
    /// <para>连接所使用的插件、连接器与认证方案。</para>
    /// <para>The plugin, connector and auth scheme a connection uses.</para>
    /// </param>
    /// <param name="scopedConfiguration">
    /// <para>该连接器与认证方案下的非密钥安装配置，按字段 Id 索引。</para>
    /// <para>The non-secret setup configuration of that connector and auth scheme, keyed by field ID.</para>
    /// </param>
    public bool IsConfiguredFor(
        ResolvedIntegrationDefinition definition,
        IReadOnlyDictionary<string, string?> scopedConfiguration
    ) =>
        definition
            .AuthScheme.InstallationFields.Where(field => field.IsRequired)
            .All(field =>
                field.Type == FormFieldType.Secret
                    ? FindCredential(GetSlot(definition, field.Id)) != null
                    : scopedConfiguration.TryGetValue(field.Id, out var value) && !string.IsNullOrWhiteSpace(value)
            );

    private static string GetSlot(ResolvedIntegrationDefinition definition, string fieldId) =>
        IntegrationCredentialSlots.InstallationField(definition.Connector.Id, definition.AuthScheme.Id, fieldId);

    private PluginInstallationCredential? FindCredential(string slot) =>
        _installation.Credentials.FirstOrDefault(credential =>
            string.Equals(credential.Slot, slot, StringComparison.OrdinalIgnoreCase)
        );
}
