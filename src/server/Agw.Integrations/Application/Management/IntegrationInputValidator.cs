using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Application.Management;

internal static class IntegrationInputValidator
{
    public static ValidatedIntegrationInput Validate(
        IReadOnlyList<FormFieldDefinition> fields,
        IDictionary<string, string?>? configuration,
        IDictionary<string, SecretFieldUpdateRequest>? secretUpdates,
        IReadOnlyCollection<string> existingCredentialSlots,
        Func<string, string> slotFactory,
        bool allowClearingExistingRequiredSecrets = false,
        bool allowMissingRequiredFields = false
    )
    {
        configuration ??= new Dictionary<string, string?>();
        secretUpdates ??= new Dictionary<string, SecretFieldUpdateRequest>();
        var fieldsById = fields.ToDictionary(field => field.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var key in configuration.Keys)
        {
            if (!fieldsById.TryGetValue(key, out var field) || field.Type == FormFieldType.Secret)
            {
                throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
            }
        }

        foreach (var key in secretUpdates.Keys)
        {
            if (!fieldsById.TryGetValue(key, out var field) || field.Type != FormFieldType.Secret)
            {
                throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
            }
        }

        var normalizedConfiguration = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var secretsToSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clearedSecretFieldIds = new List<string>();
        foreach (var field in fields)
        {
            if (field.Type == FormFieldType.Secret)
            {
                var hasExisting = existingCredentialSlots.Contains(
                    slotFactory(field.Id),
                    StringComparer.OrdinalIgnoreCase
                );
                var update = secretUpdates.TryGetValue(field.Id, out var requested)
                    ? requested
                    : new SecretFieldUpdateRequest { Action = SecretUpdateAction.Keep };
                ValidateSecretUpdate(update);
                var configured = update.Action switch
                {
                    SecretUpdateAction.Set => true,
                    SecretUpdateAction.Clear => false,
                    _ => hasExisting,
                };
                var explicitlyClearingExisting =
                    allowClearingExistingRequiredSecrets && hasExisting && update.Action == SecretUpdateAction.Clear;
                if (field.IsRequired && !configured && !explicitlyClearingExisting && !allowMissingRequiredFields)
                {
                    throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
                }

                if (update.Action == SecretUpdateAction.Set)
                {
                    secretsToSet[field.Id] = update.SecretValue!;
                }
                else if (update.Action == SecretUpdateAction.Clear)
                {
                    clearedSecretFieldIds.Add(field.Id);
                }

                continue;
            }

            configuration.TryGetValue(field.Id, out var value);
            value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (field.IsRequired && value == null && !allowMissingRequiredFields)
            {
                throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
            }

            if (
                value != null
                && field.Type == FormFieldType.Url
                && (
                    !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                )
            )
            {
                throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
            }

            if (value != null)
            {
                normalizedConfiguration[field.Id] = value;
            }
        }

        return new ValidatedIntegrationInput(normalizedConfiguration, secretsToSet, clearedSecretFieldIds);
    }

    private static void ValidateSecretUpdate(SecretFieldUpdateRequest update)
    {
        if (!Enum.IsDefined(update.Action))
        {
            throw new AgwException(ErrorCodes.IntegrationSecretMutationInvalid);
        }

        var hasSecretValue = !string.IsNullOrWhiteSpace(update.SecretValue);
        if (update.Action != SecretUpdateAction.Set)
        {
            if (hasSecretValue)
            {
                throw new AgwException(ErrorCodes.IntegrationSecretMutationInvalid);
            }

            return;
        }

        if (!hasSecretValue)
        {
            throw new AgwException(ErrorCodes.IntegrationSecretMutationInvalid);
        }
    }
}

internal sealed class ValidatedIntegrationInput
{
    public ValidatedIntegrationInput(
        IReadOnlyDictionary<string, string?> configuration,
        IReadOnlyDictionary<string, string> secretsToSet,
        IReadOnlyCollection<string> clearedSecretFieldIds
    )
    {
        Configuration = configuration;
        SecretsToSet = secretsToSet;
        ClearedSecretFieldIds = clearedSecretFieldIds;
    }

    public IReadOnlyDictionary<string, string?> Configuration { get; }
    public IReadOnlyDictionary<string, string> SecretsToSet { get; }
    public IReadOnlyCollection<string> ClearedSecretFieldIds { get; }
}
