using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.Database;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionRegistration : IExtensionRegistration
{
    private IServiceCollection? _services;
    private readonly HashSet<string> _endpointIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _endpointRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _toolIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _toolNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skillIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _skillNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _backgroundServiceTypes = new(StringComparer.Ordinal);
    private readonly List<RegisteredEndpoint> _endpoints = [];
    private readonly List<RegisteredTool> _tools = [];
    private readonly List<RegisteredSkill> _skills = [];
    private readonly List<Type> _backgroundServiceTypesToStart = [];
    private readonly List<JsonSerializerContext> _jsonContexts = [];
    private JsonSerializerOptions? _jsonOptions;
    private IExtensionDatabaseFactory? _databaseFactory;

    public ExtensionRegistration(IServiceCollection services)
    {
        _services = services;
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new ExtensionJsonTypeInfoResolver(_jsonContexts),
        };
    }

    public JsonSerializerOptions JsonOptions => _jsonOptions ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);

    public void RegisterJsonContext(JsonSerializerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _jsonContexts.Add(context);
    }

    public void EnforceJsonTypeInfoResolver()
    {
        JsonSerializerOptions options = JsonOptions;
        if (options.TypeInfoResolver is not ExtensionJsonTypeInfoResolver)
        {
            options.TypeInfoResolver = new ExtensionJsonTypeInfoResolver(_jsonContexts);
        }
    }

    public IReadOnlyList<RegisteredEndpoint> Endpoints => _endpoints;

    public IReadOnlyList<RegisteredTool> Tools => _tools;

    public IReadOnlyList<RegisteredSkill> Skills => _skills;

    public IReadOnlyList<Type> BackgroundServiceTypes => _backgroundServiceTypesToStart;

    public IExtensionDatabaseFactory? DatabaseFactory => _databaseFactory;

    public void RegisterEndpoint(ExtensionEndpointDescriptor descriptor, ExtensionEndpointHandler handler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        ValidateIdentifier(descriptor.Id);
        ValidateHttpMethod(descriptor.Method);
        ValidateRelativePath(descriptor.RelativePath);
        ValidateSchema(descriptor.RequestSchema);
        ValidateSchema(descriptor.ResponseSchema);
        string routeKey = $"{descriptor.Method} {descriptor.RelativePath}";
        if (!_endpointIds.Add(descriptor.Id) || !_endpointRoutes.Add(routeKey))
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "Extension endpoint registration is duplicated."
            );

        ExtensionEndpointDescriptor stableDescriptor = descriptor with
        {
            RequestSchema = descriptor.RequestSchema?.Clone(),
            ResponseSchema = descriptor.ResponseSchema?.Clone(),
        };
        _endpoints.Add(new RegisteredEndpoint(stableDescriptor, handler));
    }

    public void RegisterTool<TTool>(ExtensionToolDescriptor descriptor)
        where TTool : class, IExtensionTool
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateIdentifier(descriptor.Id);
        ValidateIdentifier(descriptor.Name);
        if (
            !Enum.IsDefined(descriptor.RequiredPermission)
            || descriptor.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Object
        )
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension tool metadata is invalid.");
        if (!_toolIds.Add(descriptor.Id) || !_toolNames.Add(descriptor.Name))
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension tool registration is duplicated.");
        ValidateSchema(descriptor.InputSchema);

        Services.AddScoped(typeof(TTool));
        ExtensionToolDescriptor stableDescriptor = descriptor with { InputSchema = descriptor.InputSchema.Clone() };
        _tools.Add(new RegisteredTool(typeof(TTool), stableDescriptor));
    }

    public void RegisterSkill(ExtensionSkillDescriptor descriptor, ExtensionSkillContentProvider contentProvider)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(contentProvider);
        ValidateIdentifier(descriptor.Id);
        if (
            !AgentSkillFrontmatter.ValidateName(descriptor.Name, out _)
            || !AgentSkillFrontmatter.ValidateDescription(descriptor.Description, out _)
        )
        {
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension skill metadata is invalid.");
        }
        if (_skillIds.Contains(descriptor.Id) || _skillNames.Contains(descriptor.Name))
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension skill registration is duplicated.");
        _skillIds.Add(descriptor.Id);
        _skillNames.Add(descriptor.Name);
        _skills.Add(new RegisteredSkill(descriptor, contentProvider));
    }

    public void RegisterBackgroundService<TService>()
        where TService : class, IExtensionBackgroundService
    {
        string typeName = typeof(TService).FullName ?? typeof(TService).Name;
        if (!_backgroundServiceTypes.Add(typeName))
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension background service is duplicated.");
        Services.AddSingleton<TService>();
        _backgroundServiceTypesToStart.Add(typeof(TService));
    }

    public void RegisterDatabase(IExtensionDatabaseFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_databaseFactory is not null)
            throw new AgwException(
                ErrorCodes.ExtensionPackageInvalid,
                "Extension database context registration is invalid."
            );
        _databaseFactory = factory;
    }

    public void Clear()
    {
        _services?.Clear();
        _services = null;
        _endpoints.Clear();
        _tools.Clear();
        _skills.Clear();
        _backgroundServiceTypesToStart.Clear();
        _jsonContexts.Clear();
        _databaseFactory = null;
        _jsonOptions = null;
        _endpointIds.Clear();
        _endpointRoutes.Clear();
        _toolIds.Clear();
        _toolNames.Clear();
        _skillIds.Clear();
        _skillNames.Clear();
        _backgroundServiceTypes.Clear();
    }

    private IServiceCollection Services => _services ?? throw new AgwException(ErrorCodes.ExtensionUnavailable);

    private static void ValidateSchema(JsonElement? element)
    {
        if (element is null)
            return;
        if (element.Value.ValueKind != JsonValueKind.Object)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension schema is invalid.");
        var schema = OpenApiModelFactory.Parse<OpenApiSchema>(
            element.Value.GetRawText(),
            OpenApiSpecVersion.OpenApi3_1,
            new OpenApiDocument(),
            out var diagnostic,
            "json"
        );
        if (schema is null || diagnostic.Errors.Count > 0)
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension schema is invalid.");
    }

    private static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension registration identifier is invalid.");
    }

    private static void ValidateHttpMethod(string method)
    {
        if (
            string.IsNullOrWhiteSpace(method)
            || method != method.ToUpperInvariant()
            || method.Any(character => !char.IsAsciiLetter(character))
        )
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension endpoint method is invalid.");
    }

    private static void ValidateRelativePath(string? relativePath)
    {
        if (
            string.IsNullOrWhiteSpace(relativePath)
            || relativePath != relativePath.Trim()
            || relativePath.Contains('\\')
            || relativePath.Contains('%')
            || relativePath.Contains(':')
            || relativePath.StartsWith('/')
            || relativePath.EndsWith('/')
            || relativePath.Contains('?')
            || relativePath.Contains('#')
            || relativePath.Any(char.IsControl)
            || relativePath
                .Split('/')
                .Any(segment =>
                    string.IsNullOrEmpty(segment)
                    || segment is "." or ".."
                    || segment.Contains('{')
                    || segment.Contains('}')
                )
        )
            throw new AgwException(ErrorCodes.ExtensionPackageInvalid, "Extension endpoint path is invalid.");
    }
}

internal sealed record RegisteredEndpoint(ExtensionEndpointDescriptor Descriptor, ExtensionEndpointHandler Handler);

internal sealed record RegisteredTool(Type ToolType, ExtensionToolDescriptor Descriptor);

internal sealed record RegisteredSkill(
    ExtensionSkillDescriptor Descriptor,
    ExtensionSkillContentProvider ContentProvider
);
