using System.Text.RegularExpressions;
using Agw.Integrations.Domain.Plugins;

namespace Agw.Integrations.Application.Plugins;

public static class PluginCatalogValidator
{
    private static readonly HashSet<string> ReservedAuthorizeParameterNames = new(
        ["client_id", "response_type", "redirect_uri", "state", "scope", "code_challenge", "code_challenge_method"],
        StringComparer.Ordinal
    );
    private static readonly Regex IdPattern = new(
        "^[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*$",
        RegexOptions.CultureInvariant
    );
    private static readonly Regex HttpHeaderNamePattern = new(
        "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$",
        RegexOptions.CultureInvariant
    );

    public static void Validate(IReadOnlyList<PluginDefinition> plugins)
    {
        ValidateIds(plugins.Select(plugin => plugin.Id), "plugin");

        foreach (var plugin in plugins)
        {
            RequireValue(plugin.Version, $"Plugin '{plugin.Id}' requires a version.");
            RequireValue(plugin.DisplayName, $"Plugin '{plugin.Id}' requires a display name.");
            ValidateIds(plugin.Connectors.Select(connector => connector.Id), $"connector in plugin '{plugin.Id}'");

            foreach (var skill in plugin.Skills)
            {
                ValidateSkillPath(plugin.Id, skill);
            }

            foreach (var connector in plugin.Connectors)
            {
                ValidateConnector(plugin.Id, connector);
            }
        }
    }

    private static void ValidateConnector(string pluginId, ConnectorDefinition connector)
    {
        var context = $"connector '{pluginId}/{connector.Id}'";
        RequireValue(connector.DisplayName, $"{context} requires a display name.");
        ValidateIds(connector.AuthSchemes.Select(authScheme => authScheme.Id), $"auth scheme in {context}");
        ValidateIds(connector.CapabilitySources.Select(source => source.Id), $"capability source in {context}");

        foreach (var authScheme in connector.AuthSchemes)
        {
            var fields = authScheme.InstallationFields.Concat(authScheme.ConnectionFields).ToList();
            ValidateIds(fields.Select(field => field.Id), $"field in auth scheme '{authScheme.Id}' in {context}");

            foreach (var field in fields)
            {
                RequireValue(
                    field.Label,
                    $"Field '{field.Id}' in auth scheme '{authScheme.Id}' in {context} requires a label."
                );
                if (!Enum.IsDefined(field.Type))
                {
                    throw Invalid(
                        $"Field '{field.Id}' in auth scheme '{authScheme.Id}' in {context} has an invalid type."
                    );
                }
            }

            ValidateAuthScheme(context, authScheme);
        }

        var authSchemes = connector.AuthSchemes.ToDictionary(
            authScheme => authScheme.Id,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var source in connector.CapabilitySources)
        {
            ValidateCapabilitySource(context, source, authSchemes);
        }
    }

    private static void ValidateAuthScheme(string context, AuthSchemeDefinition authScheme)
    {
        RequireValue(authScheme.DisplayName, $"Auth scheme '{authScheme.Id}' in {context} requires a display name.");
        if (!Enum.IsDefined(authScheme.Type))
        {
            throw Invalid($"Auth scheme '{authScheme.Id}' in {context} has an invalid type.");
        }

        if (authScheme.Type == AuthSchemeType.OAuth2)
        {
            var settings =
                authScheme.OAuth2AuthorizationCode
                ?? throw Invalid($"OAuth auth scheme '{authScheme.Id}' in {context} requires OAuth settings.");

            ValidateAbsoluteEndpoint(settings.AuthorizationEndpoint, "authorization", authScheme.Id, context);
            ValidateAbsoluteEndpoint(settings.TokenEndpoint, "token", authScheme.Id, context);
            ValidateOAuthFields(context, authScheme, settings);
            ValidateSubjectResolution(context, authScheme.Id, settings);

            if (!Enum.IsDefined(settings.ClientAuthenticationMethod))
            {
                throw Invalid(
                    $"OAuth auth scheme '{authScheme.Id}' in {context} has an invalid client authentication method."
                );
            }

            ValidateParameterKeys(settings.AdditionalAuthorizeParameters, "authorize", authScheme.Id, context);
            ValidateNoReservedAuthorizeParameters(settings.AdditionalAuthorizeParameters, authScheme.Id, context);
            ValidateParameterKeys(settings.AdditionalTokenParameters, "token", authScheme.Id, context);
            return;
        }

        if (authScheme.OAuth2AuthorizationCode is not null)
        {
            throw Invalid($"Non-OAuth auth scheme '{authScheme.Id}' in {context} cannot declare OAuth settings.");
        }
    }

    private static void ValidateCapabilitySource(
        string context,
        CapabilitySourceDefinition source,
        IReadOnlyDictionary<string, AuthSchemeDefinition> authSchemes
    )
    {
        if (source is NativeCapabilitySourceDefinition nativeSource)
        {
            RequireValue(
                nativeSource.Provider,
                $"Native capability source '{source.Id}' in {context} requires a provider."
            );
            return;
        }

        if (source is not McpCapabilitySourceDefinition mcpSource)
        {
            throw Invalid($"Capability source '{source.Id}' in {context} has an unsupported type.");
        }

        switch (mcpSource.Transport)
        {
            case StdioMcpTransportDefinition stdio:
                RequireValue(stdio.Command, $"Stdio MCP source '{source.Id}' in {context} requires a command.");
                break;
            case HttpMcpTransportDefinition http:
                ValidateAbsoluteEndpoint(http.Endpoint, "HTTP MCP", source.Id, context);
                break;
            case SseMcpTransportDefinition sse:
                ValidateAbsoluteEndpoint(sse.Endpoint, "SSE MCP", source.Id, context);
                break;
            default:
                throw Invalid($"MCP source '{source.Id}' in {context} requires a supported transport.");
        }

        if (mcpSource.CredentialBindings.Count > 0)
        {
            ValidateCredentialTransport(context, source.Id, mcpSource.Transport);
        }

        var bindingTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in mcpSource.CredentialBindings)
        {
            if (!Enum.IsDefined(binding.Target))
            {
                throw Invalid($"Credential binding in MCP source '{source.Id}' in {context} has an invalid target.");
            }

            RequireValue(
                binding.TargetName,
                $"Credential binding in MCP source '{source.Id}' in {context} requires a target name."
            );
            ValidateBindingTargetName(context, source.Id, binding.Target, binding.TargetName);
            if (!bindingTargets.Add($"{binding.Target}:{binding.TargetName}"))
            {
                throw Invalid(
                    $"MCP source '{source.Id}' in {context} has duplicate credential target "
                        + $"'{binding.Target}:{binding.TargetName}'."
                );
            }

            ValidateBindingTarget(context, source.Id, mcpSource.Transport, binding.Target);
            ValidateBindingPrefix(context, source.Id, binding.ValuePrefix);
            ValidateBindingValueSource(context, source.Id, binding.ValueSource, authSchemes);
        }
    }

    private static void ValidateCredentialTransport(string context, string sourceId, McpTransportDefinition transport)
    {
        var endpoint = transport switch
        {
            HttpMcpTransportDefinition http => http.Endpoint,
            SseMcpTransportDefinition sse => sse.Endpoint,
            _ => null,
        };
        if (
            endpoint != null
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw Invalid($"Credential-bound MCP source '{sourceId}' in {context} requires an HTTPS endpoint.");
        }
    }

    private static void ValidateBindingTargetName(
        string context,
        string sourceId,
        CredentialBindingTarget target,
        string targetName
    )
    {
        var valid = target switch
        {
            CredentialBindingTarget.HttpHeader => targetName.Length <= 256 && HttpHeaderNamePattern.IsMatch(targetName),
            CredentialBindingTarget.EnvironmentVariable => targetName.Length <= 512
                && targetName.IndexOfAny(['=', '\r', '\n', '\0']) < 0,
            _ => false,
        };

        if (!valid)
        {
            throw Invalid(
                $"Credential binding target name '{targetName}' is invalid for MCP source '{sourceId}' in {context}."
            );
        }
    }

    private static void ValidateOAuthFields(
        string context,
        AuthSchemeDefinition authScheme,
        OAuth2AuthorizationCodeSettings settings
    )
    {
        var installationFields = authScheme.InstallationFields.ToDictionary(
            field => field.Id,
            StringComparer.OrdinalIgnoreCase
        );
        RequireValue(
            settings.ClientIdFieldId,
            $"OAuth auth scheme '{authScheme.Id}' in {context} requires a client ID field mapping."
        );

        if (!installationFields.ContainsKey(settings.ClientIdFieldId))
        {
            throw Invalid(
                $"OAuth auth scheme '{authScheme.Id}' in {context} references unknown client ID field '{settings.ClientIdFieldId}'."
            );
        }

        if (
            settings.ClientAuthenticationMethod
            is OAuth2ClientAuthenticationMethod.Body
                or OAuth2ClientAuthenticationMethod.Basic
        )
        {
            RequireValue(
                settings.ClientSecretFieldId,
                $"OAuth auth scheme '{authScheme.Id}' in {context} requires a client secret field mapping."
            );
        }

        if (settings.ClientSecretFieldId is null)
        {
            return;
        }

        if (!installationFields.TryGetValue(settings.ClientSecretFieldId, out var clientSecretField))
        {
            throw Invalid(
                $"OAuth auth scheme '{authScheme.Id}' in {context} references unknown client secret field '{settings.ClientSecretFieldId}'."
            );
        }

        if (clientSecretField.Type != FormFieldType.Secret)
        {
            throw Invalid($"OAuth client secret field '{clientSecretField.Id}' in {context} must be a secret field.");
        }
    }

    private static void ValidateSubjectResolution(
        string context,
        string authSchemeId,
        OAuth2AuthorizationCodeSettings settings
    )
    {
        var subject =
            settings.SubjectResolution
            ?? throw Invalid($"OAuth auth scheme '{authSchemeId}' in {context} requires subject resolution settings.");

        if (!Enum.IsDefined(subject.Source))
        {
            throw Invalid($"OAuth auth scheme '{authSchemeId}' in {context} has an invalid subject source.");
        }

        RequireValue(subject.Field, $"OAuth auth scheme '{authSchemeId}' in {context} requires a subject field.");
        if (subject.Source == OAuthSubjectSource.UserInfo)
        {
            ValidateAbsoluteEndpoint(settings.UserInfoEndpoint ?? string.Empty, "user info", authSchemeId, context);
        }
        else if (!string.IsNullOrWhiteSpace(settings.UserInfoEndpoint))
        {
            ValidateAbsoluteEndpoint(settings.UserInfoEndpoint, "user info", authSchemeId, context);
        }
    }

    private static void ValidateBindingTarget(
        string context,
        string sourceId,
        McpTransportDefinition transport,
        CredentialBindingTarget target
    )
    {
        var valid = transport switch
        {
            StdioMcpTransportDefinition => target == CredentialBindingTarget.EnvironmentVariable,
            HttpMcpTransportDefinition or SseMcpTransportDefinition => target == CredentialBindingTarget.HttpHeader,
            _ => false,
        };

        if (!valid)
        {
            throw Invalid(
                $"Credential binding target '{target}' is not valid for MCP source '{sourceId}' in {context}."
            );
        }
    }

    private static void ValidateBindingPrefix(string context, string sourceId, string? prefix)
    {
        if (prefix is null)
        {
            return;
        }

        if (prefix.Length > 128 || prefix.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw Invalid($"Credential binding prefix in MCP source '{sourceId}' in {context} is invalid.");
        }
    }

    private static void ValidateBindingValueSource(
        string context,
        string sourceId,
        CredentialValueSourceDefinition valueSource,
        IReadOnlyDictionary<string, AuthSchemeDefinition> authSchemes
    )
    {
        if (valueSource is null)
        {
            throw Invalid($"Credential binding in MCP source '{sourceId}' in {context} requires a value source.");
        }

        RequireValue(
            valueSource.AuthSchemeId,
            $"Credential binding in MCP source '{sourceId}' in {context} requires an auth scheme ID."
        );

        if (!authSchemes.TryGetValue(valueSource.AuthSchemeId, out var authScheme))
        {
            throw Invalid(
                $"Credential binding in MCP source '{sourceId}' in {context} references unknown auth scheme '{valueSource.AuthSchemeId}'."
            );
        }

        IReadOnlyList<FormFieldDefinition> fields;
        string fieldId;
        switch (valueSource)
        {
            case ConnectionFieldCredentialValueSourceDefinition connectionField:
                fields = authScheme.ConnectionFields;
                fieldId = connectionField.FieldId;
                break;
            case InstallationFieldCredentialValueSourceDefinition installationField:
                fields = authScheme.InstallationFields;
                fieldId = installationField.FieldId;
                break;
            case OAuthAccessTokenCredentialValueSourceDefinition:
                if (authScheme.Type != AuthSchemeType.OAuth2)
                {
                    throw Invalid(
                        $"OAuth access token binding in MCP source '{sourceId}' in {context} must reference an OAuth auth scheme."
                    );
                }

                return;
            default:
                throw Invalid(
                    $"Credential binding in MCP source '{sourceId}' in {context} has an unsupported value source."
                );
        }

        RequireValue(fieldId, $"Credential binding in MCP source '{sourceId}' in {context} requires a field ID.");
        if (!fields.Any(field => string.Equals(field.Id, fieldId, StringComparison.OrdinalIgnoreCase)))
        {
            throw Invalid(
                $"Credential binding in MCP source '{sourceId}' in {context} references unknown field '{fieldId}' "
                    + $"for auth scheme '{authScheme.Id}'."
            );
        }
    }

    private static void ValidateSkillPath(string pluginId, PluginSkillDefinition skill)
    {
        RequireValue(skill.ContentPath, $"Plugin '{pluginId}' skill requires a content path.");

        var path = skill.ContentPath;
        var isDrivePath = path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
        var hasParentSegment = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));

        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\') || isDrivePath || hasParentSegment)
        {
            throw Invalid($"Plugin '{pluginId}' skill must use a safe relative content path.");
        }
    }

    private static void ValidateIds(IEnumerable<string> ids, string itemType)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            RequireValue(id, $"{itemType} ID cannot be empty.");
            if (!IdPattern.IsMatch(id))
            {
                throw Invalid($"{itemType} ID '{id}' has an invalid format.");
            }

            if (!seen.Add(id))
            {
                throw Invalid($"Duplicate {itemType} ID '{id}'.");
            }
        }
    }

    private static void ValidateAbsoluteEndpoint(string endpoint, string endpointType, string id, string context)
    {
        if (
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        )
        {
            throw Invalid($"{endpointType} endpoint for '{id}' in {context} must be an absolute HTTP URL.");
        }
    }

    private static void ValidateParameterKeys(
        IReadOnlyDictionary<string, string> parameters,
        string parameterType,
        string authSchemeId,
        string context
    )
    {
        if (parameters.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw Invalid(
                $"OAuth auth scheme '{authSchemeId}' in {context} has an empty {parameterType} parameter name."
            );
        }
    }

    private static void ValidateNoReservedAuthorizeParameters(
        IReadOnlyDictionary<string, string> parameters,
        string authSchemeId,
        string context
    )
    {
        var reservedParameter = parameters.Keys.FirstOrDefault(ReservedAuthorizeParameterNames.Contains);
        if (reservedParameter != null)
        {
            throw Invalid(
                $"OAuth auth scheme '{authSchemeId}' in {context} cannot override reserved authorize parameter "
                    + $"'{reservedParameter}'."
            );
        }
    }

    private static void RequireValue(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid(message);
        }
    }

    private static InvalidOperationException Invalid(string message)
    {
        return new InvalidOperationException($"Invalid plugin catalog: {message}");
    }
}
