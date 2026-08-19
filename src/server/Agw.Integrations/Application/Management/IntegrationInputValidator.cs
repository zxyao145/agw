using System.Text.RegularExpressions;
using Agw.Integrations.Contracts.Management;
using Agw.Integrations.Domain.Plugins;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Application.Management;

internal static partial class IntegrationInputValidator
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
        var normalizedSecretUpdates = new Dictionary<string, SecretFieldUpdateRequest>(
            StringComparer.OrdinalIgnoreCase
        );
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

                normalizedSecretUpdates[field.Id] = update;
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

        return new ValidatedIntegrationInput(normalizedConfiguration, normalizedSecretUpdates);
    }

    public static string NormalizeAlias(string alias)
    {
        var normalized = (alias ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > 128 || !AliasRegex().IsMatch(normalized))
        {
            throw new AgwException(ErrorCodes.IntegrationAliasInvalid);
        }

        return normalized;
    }

    public static string RequireDisplayName(string displayName)
    {
        var normalized = (displayName ?? string.Empty).Trim();
        if (normalized.Length == 0 || normalized.Length > 200)
        {
            throw new AgwException(ErrorCodes.IntegrationConfigurationInvalid);
        }

        return normalized;
    }

    private static void ValidateSecretUpdate(SecretFieldUpdateRequest update)
    {
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

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasRegex();
}

internal sealed class ValidatedIntegrationInput
{
    public ValidatedIntegrationInput(
        IReadOnlyDictionary<string, string?> configuration,
        IReadOnlyDictionary<string, SecretFieldUpdateRequest> secretUpdates
    )
    {
        Configuration = configuration;
        SecretUpdates = secretUpdates;
    }

    public IReadOnlyDictionary<string, string?> Configuration { get; }
    public IReadOnlyDictionary<string, SecretFieldUpdateRequest> SecretUpdates { get; }
}
