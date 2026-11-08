using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Tools.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Semver;

namespace Agw.Extensions.Runtime.Tests.Fixture;

public sealed class TestExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterJsonContext(FixtureJsonContext.Default);
        using JsonDocument echoSchema = JsonDocument.Parse(
            """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}"""
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "echo",
                Method = "POST",
                RelativePath = "echo",
                RequestSchema = echoSchema.RootElement.Clone(),
                ResponseSchema = echoSchema.RootElement.Clone(),
            },
            static (_, context, request, _) =>
                ValueTask.FromResult(
                    ExtensionEndpointResponse.FromJson(
                        JsonSerializer.SerializeToElement(
                            new EchoResponse(request.Body!.Value.GetProperty("value").GetString()!),
                            context.JsonOptions
                        )
                    )
                )
        );
        using JsonDocument toolSchema = JsonDocument.Parse(
            """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}"""
        );
        registration.RegisterTool<CalendarLookupTool>(
            new ExtensionToolDescriptor
            {
                Id = "calendar-lookup",
                Name = "calendar_lookup",
                Description = "Looks up calendar information.",
                RequiredPermission = AgwToolPermission.ReadOnly,
                InputSchema = toolSchema.RootElement.Clone(),
                Category = "Calendar",
                AllowInPlanMode = true,
            }
        );
        registration.RegisterTool<CalendarAdminTool>(
            new ExtensionToolDescriptor
            {
                Id = "calendar-admin",
                Name = "calendar_admin",
                Description = "Updates calendar information.",
                RequiredPermission = AgwToolPermission.Write,
                InputSchema = toolSchema.RootElement.Clone(),
                Category = "Calendar",
                AllowInPlanMode = false,
            }
        );
        registration.RegisterBackgroundService<FixtureBackgroundService>();
        registration.RegisterSkill(
            new ExtensionSkillDescriptor("calendar-skill", "calendar", "Calendar instructions."),
            static (_, _, _) => ValueTask.FromResult("Calendar instructions")
        );
        registration.RegisterSkill(
            new ExtensionSkillDescriptor("admin-skill", "calendar-admin", "Admin instructions."),
            static (_, _, _) => ValueTask.FromResult("Admin instructions")
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "stream",
                Method = "GET",
                RelativePath = "stream",
            },
            static (_, _, _, _) => ValueTask.FromResult(ExtensionEndpointResponse.FromStream(StreamResponseAsync()))
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "error",
                Method = "GET",
                RelativePath = "error",
            },
            static (_, _, _, _) => throw new FixtureException("fixture failure")
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "log",
                Method = "GET",
                RelativePath = "log",
            },
            static (_, context, _, _) =>
            {
                context.Logger.LogInformation("Extension payload {Payload}", new EchoResponse("private payload"));
                context.Logger.LogError(new FixtureException("private exception"), "Extension failure details");
                return ValueTask.FromResult(
                    ExtensionEndpointResponse.FromJson(
                        JsonSerializer.SerializeToElement(new EchoResponse("logged"), context.JsonOptions)
                    )
                );
            }
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "runtime-capability",
                Method = "GET",
                RelativePath = "runtime-capability",
            },
            static (_, context, _, _) =>
            {
                _ = context.HostCapabilities.GetRequired<IExtensionRuntime>();
                return ValueTask.FromResult(
                    ExtensionEndpointResponse.FromJson(JsonSerializer.SerializeToElement(new { available = true }))
                );
            }
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamResponseAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        yield return "{\"chunk\":1}"u8.ToArray();
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        yield return "{\"chunk\":2}"u8.ToArray();
    }

    private sealed class FixtureException(string message) : Exception(message);

    private sealed class CalendarLookupTool : IExtensionTool
    {
        public string Name => "calendar_lookup";

        public AgwToolPermission RequiredPermission => AgwToolPermission.ReadOnly;

        public bool AllowInPlanMode => true;

        public AIFunction CreateFunction(JsonSerializerOptions serializerOptions) =>
            AIFunctionFactory.Create(
                (Func<string, string>)(value => $"calendar:{value}"),
                new AIFunctionFactoryOptions { Name = Name, SerializerOptions = serializerOptions }
            );
    }

    private sealed class CalendarAdminTool : IExtensionTool
    {
        public string Name => "calendar_admin";

        public AgwToolPermission RequiredPermission => AgwToolPermission.Write;

        public bool AllowInPlanMode => false;

        public AIFunction CreateFunction(JsonSerializerOptions serializerOptions) =>
            AIFunctionFactory.Create(
                (Func<string, string>)(value => $"updated:{value}"),
                new AIFunctionFactoryOptions { Name = Name, SerializerOptions = serializerOptions }
            );
    }

    private sealed class FixtureBackgroundService : IExtensionBackgroundService
    {
        public ValueTask StartAsync(
            IExtensionContext context,
            IExtensionBackgroundScheduler scheduler,
            CancellationToken cancellationToken
        )
        {
            File.WriteAllText(Path.Combine(context.DataDirectory, "background-started.txt"), "started");
            return ValueTask.CompletedTask;
        }

        public ValueTask PauseAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask ResumeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

public sealed class CurrentTestExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterJsonContext(FixtureJsonContext.Default);
        using JsonDocument toolSchema = JsonDocument.Parse(
            """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"]}"""
        );
        registration.RegisterTool<CurrentCalendarLookupTool>(
            new ExtensionToolDescriptor
            {
                Id = "calendar-lookup",
                Name = "calendar_lookup",
                Description = "Looks up current calendar information.",
                RequiredPermission = AgwToolPermission.Write,
                InputSchema = toolSchema.RootElement.Clone(),
                Category = "Calendar",
                AllowInPlanMode = false,
            }
        );
        registration.RegisterSkill(
            new ExtensionSkillDescriptor("calendar-skill", "calendar", "Current calendar instructions."),
            static (_, _, _) => ValueTask.FromResult("Current calendar instructions")
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    private sealed class CurrentCalendarLookupTool : IExtensionTool
    {
        public string Name => "calendar_lookup";

        public AgwToolPermission RequiredPermission => AgwToolPermission.Write;

        public bool AllowInPlanMode => false;

        public AIFunction CreateFunction(JsonSerializerOptions serializerOptions) =>
            AIFunctionFactory.Create(
                (Func<string, string>)(value => $"current-calendar:{value}"),
                new AIFunctionFactoryOptions { Name = Name, SerializerOptions = serializerOptions }
            );
    }
}

public sealed class TestExtensionWithoutTools : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterJsonContext(FixtureJsonContext.Default);
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "echo",
                Method = "POST",
                RelativePath = "echo",
            },
            static (_, context, request, _) =>
                ValueTask.FromResult(
                    ExtensionEndpointResponse.FromJson(
                        JsonSerializer.SerializeToElement(
                            new EchoResponse(request.Body!.Value.GetProperty("value").GetString()!),
                            context.JsonOptions
                        )
                    )
                )
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "not-found",
                Method = "GET",
                RelativePath = "not-found",
            },
            static (_, context, _, _) =>
                ValueTask.FromResult(
                    ExtensionEndpointResponse.FromJson(
                        JsonSerializer.SerializeToElement(new EchoResponse("missing"), context.JsonOptions),
                        statusCode: 404
                    )
                )
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class TestExtensionWithoutJsonContext : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "echo",
                Method = "POST",
                RelativePath = "echo",
            },
            static (_, context, request, _) =>
                ValueTask.FromResult(
                    ExtensionEndpointResponse.FromJson(
                        JsonSerializer.SerializeToElement(
                            new EchoResponse(request.Body!.Value.GetProperty("value").GetString()!),
                            context.JsonOptions
                        )
                    )
                )
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "echo-list",
                Method = "POST",
                RelativePath = "echo-list",
            },
            static (_, context, request, _) =>
                ValueTask.FromResult(
                    ExtensionEndpointResponse.FromJson(
                        JsonSerializer.SerializeToElement(
                            new List<EchoResponse> { new(request.Body!.Value.GetProperty("value").GetString()!) },
                            context.JsonOptions
                        )
                    )
                )
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public abstract class InvalidEndpointPathExtension(string? relativePath) : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration) =>
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "invalid-path",
                Method = "GET",
                RelativePath = relativePath!,
            },
            static (_, _, _, _) =>
                ValueTask.FromResult(ExtensionEndpointResponse.FromJson(JsonSerializer.SerializeToElement(true)))
        );

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class InvalidEndpointEmptySegmentExtension() : InvalidEndpointPathExtension("items//list") { }

public sealed class InvalidEndpointEncodedTraversalExtension() : InvalidEndpointPathExtension("items/%2e%2e/list") { }

public sealed class InvalidEndpointColonExtension() : InvalidEndpointPathExtension("items:admin") { }

public sealed class InvalidEndpointControlCharacterExtension() : InvalidEndpointPathExtension("items/\u0001/list") { }

public sealed class InvalidEndpointNullPathExtension() : InvalidEndpointPathExtension(null) { }

public sealed class InvalidEndpointSchemaExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"invalid-type"}""");
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "invalid-schema",
                Method = "POST",
                RelativePath = "invalid-schema",
                RequestSchema = schema.RootElement.Clone(),
            },
            static (_, _, _, _) =>
                ValueTask.FromResult(ExtensionEndpointResponse.FromJson(JsonSerializer.SerializeToElement(true)))
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class InvalidToolSchemaExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"invalid-type"}""");
        registration.RegisterTool<InvalidInputSchemaTool>(
            new ExtensionToolDescriptor
            {
                Id = "invalid-schema",
                Name = "invalid_schema",
                Description = "An extension tool with an invalid input schema.",
                RequiredPermission = AgwToolPermission.ReadOnly,
                InputSchema = schema.RootElement.Clone(),
                Category = "Test",
                AllowInPlanMode = false,
            }
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    private sealed class InvalidInputSchemaTool : IExtensionTool
    {
        public string Name => "invalid_schema";

        public AgwToolPermission RequiredPermission => AgwToolPermission.ReadOnly;

        public bool AllowInPlanMode => false;

        public AIFunction CreateFunction(JsonSerializerOptions serializerOptions) =>
            AIFunctionFactory.Create(
                (Func<string, string>)(value => value),
                new AIFunctionFactoryOptions { Name = Name, SerializerOptions = serializerOptions }
            );
    }
}

public sealed class SystemIoHashingExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        _ = Crc32.HashToUInt32("runtime"u8);
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class SemverExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration) =>
        _ = SemVersion.Parse("1.2.3");

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class InvalidSkillNameExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration) =>
        registration.RegisterSkill(
            new ExtensionSkillDescriptor("invalid-name", "Calendar", "Calendar instructions."),
            static (_, _, _) => ValueTask.FromResult("Calendar instructions")
        );

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class InvalidSkillDescriptionExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration) =>
        registration.RegisterSkill(
            new ExtensionSkillDescriptor("invalid-description", "calendar", ""),
            static (_, _, _) => ValueTask.FromResult("Calendar instructions")
        );

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

public sealed class DuplicateSkillNameExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterSkill(
            new ExtensionSkillDescriptor("first-skill", "calendar", "First instructions."),
            static (_, _, _) => ValueTask.FromResult("First instructions")
        );
        registration.RegisterSkill(
            new ExtensionSkillDescriptor("second-skill", "calendar", "Second instructions."),
            static (_, _, _) => ValueTask.FromResult("Second instructions")
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
