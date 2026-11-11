using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Extensions.Sdk.Database;
using Agw.Extensions.Sdk.EntityFrameworkCore;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Runtime.Tests.Fixture;

public sealed class DatabaseTestExtension : IServerExtension
{
    public void ConfigureServices(IServiceCollection services, IExtensionRegistration registration)
    {
        registration.RegisterJsonContext(FixtureJsonContext.Default);
        registration.RegisterDbContext<DatabaseTestContext, DatabaseTestMigrationMarker>();
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "enqueue-outbox",
                Method = "POST",
                RelativePath = "enqueue-outbox",
            },
            static async (provider, context, _, cancellationToken) =>
            {
                DatabaseTestContext database = provider.GetRequiredService<DatabaseTestContext>();
                Guid outboxId = ExtensionOutbox.Enqueue(
                    database,
                    ExtensionOutboxKind.PublishEvent,
                    Guid.CreateVersion7().ToString("N"),
                    new CloudEventDraft { Id = Guid.CreateVersion7().ToString("N"), Type = "outbox.recovery" },
                    context.JsonOptions
                );
                await database.SaveChangesAsync(cancellationToken);
                return ExtensionEndpointResponse.FromJson(
                    JsonSerializer.SerializeToElement(new OutboxEnqueueResponse(outboxId), context.JsonOptions)
                );
            }
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "owned-record-create",
                Method = "POST",
                RelativePath = "owned-record-create",
            },
            static async (provider, context, request, cancellationToken) =>
            {
                JsonElement body = RequireBody(request);
                var record = new DatabaseOwnedProbeRecord
                {
                    Id = body.GetProperty("id").GetGuid(),
                    Value = body.GetProperty("value").GetString()!,
                };
                DatabaseTestContext database = provider.GetRequiredService<DatabaseTestContext>();
                database.OwnedProbes.Add(record);
                await database.SaveChangesAsync(cancellationToken);
                return ExtensionEndpointResponse.FromJson(
                    JsonSerializer.SerializeToElement(
                        new OwnedRecordResponse(record.Id, record.CreateBy, record.Value),
                        context.JsonOptions
                    )
                );
            }
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "owned-record-read",
                Method = "POST",
                RelativePath = "owned-record-read",
            },
            static async (provider, context, request, cancellationToken) =>
            {
                Guid recordId = RequireBody(request).GetProperty("id").GetGuid();
                var scheduler = new ExtensionOwnedRecordScheduler<DatabaseTestContext, DatabaseOwnedProbeRecord>(
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    provider.GetRequiredService<IExtensionBackgroundScheduler>()
                );
                OwnedRecordResponse result = await scheduler.ExecuteAsync(
                    recordId,
                    (_, record, _, _) =>
                        ValueTask.FromResult(new OwnedRecordResponse(record.Id, record.CreateBy, record.Value)),
                    cancellationToken
                );
                return ExtensionEndpointResponse.FromJson(
                    JsonSerializer.SerializeToElement(result, context.JsonOptions)
                );
            }
        );
        services.AddScoped<DatabaseEventHandler>();
        registration.RegisterEventHandler(
            new ExtensionEventHandlerDescriptor { Id = "database" },
            static (provider, context, delivery, cancellationToken) =>
                provider
                    .GetRequiredService<DatabaseEventHandler>()
                    .ProcessAsync(
                        provider.GetRequiredService<DatabaseTestContext>(),
                        context,
                        delivery,
                        cancellationToken
                    )
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "ef-query",
                Method = "GET",
                RelativePath = "ef-query",
            },
            static async (provider, context, _, cancellationToken) =>
            {
                DatabaseTestContext database = provider.GetRequiredService<DatabaseTestContext>();
                int count = await database.Probes.CountAsync(cancellationToken);
                return ExtensionEndpointResponse.FromJson(
                    JsonSerializer.SerializeToElement(new DatabaseProbeResponse(count), context.JsonOptions)
                );
            }
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "database",
                Method = "POST",
                RelativePath = "database",
            },
            HandleDatabaseOperationAsync
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "open-reader",
                Method = "POST",
                RelativePath = "open-reader",
            },
            static (provider, context, _, _) =>
            {
                DatabaseTestContext database = provider.GetRequiredService<DatabaseTestContext>();
                return ValueTask.FromResult(
                    ExtensionEndpointResponse.FromStream(ReadProbesAsync(database, context.JsonOptions))
                );
            }
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "hold-write",
                Method = "POST",
                RelativePath = "hold-write",
            },
            static (provider, context, request, _) =>
            {
                DatabaseTestContext database = provider.GetRequiredService<DatabaseTestContext>();
                JsonElement body = RequireBody(request);
                return ValueTask.FromResult(
                    ExtensionEndpointResponse.FromStream(
                        HoldWriteAsync(
                            database,
                            context.JsonOptions,
                            body.GetProperty("id").GetInt32(),
                            body.GetProperty("value").GetString()!
                        )
                    )
                );
            }
        );
        registration.RegisterEndpoint(
            new ExtensionEndpointDescriptor
            {
                Id = "project-default",
                Method = "GET",
                RelativePath = "project-default",
            },
            static async (_, context, _, cancellationToken) =>
            {
                IProjectDefaultResolver resolver = context.HostCapabilities.GetRequired<IProjectDefaultResolver>();
                Guid? projectId = await resolver.ResolveA2AProjectIdAsync(cancellationToken);
                return ExtensionEndpointResponse.FromJson(
                    JsonSerializer.SerializeToElement(
                        new CapabilityProbeResponse(projectId, context.CurrentUser.UserId),
                        context.JsonOptions
                    )
                );
            }
        );
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
                Id = "log",
                Method = "GET",
                RelativePath = "log",
            },
            static (_, context, _, _) =>
            {
                context.Logger.LogError("Extension payload {Payload}", new DatabaseProbeResponse(1));
                context.Logger.LogError(new DatabaseTestException("private exception"), "Extension failure details");
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
                Id = "shared-error",
                Method = "GET",
                RelativePath = "shared-error",
            },
            static (_, _, _, _) => throw new AgwException(ErrorCodes.InvalidParam, "fixture shared exception")
        );
    }

    public ValueTask StartAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask StopAsync(IExtensionContext context, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    private static async ValueTask<ExtensionEndpointResponse> HandleDatabaseOperationAsync(
        IServiceProvider serviceProvider,
        IExtensionContext context,
        ExtensionEndpointRequest request,
        CancellationToken cancellationToken
    )
    {
        DatabaseTestContext database = serviceProvider.GetRequiredService<DatabaseTestContext>();
        JsonElement body = RequireBody(request);
        string operation = body.GetProperty("operation").GetString()!;
        JsonElement response = operation switch
        {
            "invocation" => JsonSerializer.SerializeToElement(
                new DatabaseInvocationResponse(
                    serviceProvider.GetRequiredService<IExtensionUserContext>().InvocationId,
                    context.CurrentUser.UserId
                ),
                context.JsonOptions
            ),
            "model" => JsonSerializer.SerializeToElement(GetModel(database), context.JsonOptions),
            "count" => JsonSerializer.SerializeToElement(
                new DatabaseProbeResponse(await database.Probes.CountAsync(cancellationToken)),
                context.JsonOptions
            ),
            "read" => await ReadProbeAsync(database, body, context.JsonOptions, cancellationToken),
            "read-captured-query" => await ReadCapturedQueryAsync(
                database,
                body,
                context.JsonOptions,
                cancellationToken
            ),
            "get-database-values" => await GetDatabaseValuesAsync(
                database,
                body,
                context.JsonOptions,
                cancellationToken
            ),
            "find" => JsonSerializer.SerializeToElement(
                new DatabaseProbeResponse(
                    await database.Probes.AnyAsync(
                        probe => probe.Id == body.GetProperty("id").GetInt32(),
                        cancellationToken
                    )
                        ? 1
                        : 0
                ),
                context.JsonOptions
            ),
            "create" => await CreateProbeAsync(database, body, context.JsonOptions, cancellationToken),
            "update" => await UpdateProbeAsync(database, body, context.JsonOptions, cancellationToken),
            "owner-change" => await ChangeOwnerAsync(database, body, context.JsonOptions, cancellationToken),
            "attach-update" => await UpdateAttachedProbeAsync(database, body, context.JsonOptions, cancellationToken),
            "attach-delete" => await DeleteAttachedProbeAsync(database, body, context.JsonOptions, cancellationToken),
            "add-child" => await AddChildAsync(database, body, context.JsonOptions, cancellationToken),
            "add-root-child" => await AddRootAndChildAsync(database, body, context.JsonOptions, cancellationToken),
            "add-forged-child" => await AddChildWithForgedRootAsync(
                database,
                body,
                context.JsonOptions,
                cancellationToken
            ),
            "delete-root-child" => await DeleteRootAndChildAsync(
                database,
                body,
                context.JsonOptions,
                cancellationToken
            ),
            "unsupported" => await RunUnsupportedOperationAsync(database, body, cancellationToken),
            "begin-transaction" => await BeginAndDisposeTransactionAsync(
                database,
                context.JsonOptions,
                cancellationToken
            ),
            "write-transaction" => await WriteTransactionAsync(database, body, context.JsonOptions, cancellationToken),
            _ => throw new AgwException(ErrorCodes.InvalidParam),
        };
        return ExtensionEndpointResponse.FromJson(response);
    }

    private static DatabaseModelResponse GetModel(DatabaseTestContext database)
    {
        var table = database
            .Model.GetRelationalModel()
            .Tables.Single(table => table.Name == DatabaseTestSchema.TableName);
        return new DatabaseModelResponse(
            table.Name,
            table.Columns.Select(column => column.Name).Order(StringComparer.Ordinal).ToArray(),
            table.ForeignKeyConstraints.Count(),
            database.Database.GetMigrations().ToArray()
        );
    }

    private static async Task<JsonElement> CreateProbeAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        var probe = new DatabaseProbeRecord
        {
            Id = body.GetProperty("id").GetInt32(),
            Value = body.GetProperty("value").GetString()!,
            CreateBy = OptionalString(body, "createBy"),
            CreateTime = DateTimeOffset.MinValue,
            UpdateBy = OptionalString(body, "updateBy"),
            UpdateTime = DateTimeOffset.MinValue,
        };
        database.Probes.Add(probe);
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeState.From(probe), serializerOptions);
    }

    private static async Task<JsonElement> UpdateProbeAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        DatabaseProbeRecord probe = await GetProbeAsync(database, body, cancellationToken);
        probe.Value = body.GetProperty("value").GetString()!;
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeState.From(probe), serializerOptions);
    }

    private static async Task<JsonElement> ReadProbeAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        DatabaseProbeRecord probe = await GetProbeAsync(database, body, cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeState.From(probe), serializerOptions);
    }

    private static async Task<JsonElement> ReadCapturedQueryAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        int id = body.GetProperty("id").GetInt32();
        string value = body.GetProperty("value").GetString()!;
        string[] values = body.GetProperty("values").EnumerateArray().Select(item => item.GetString()!).ToArray();
        IQueryable<DatabaseProbeRecord> query = database
            .Probes.AsNoTracking()
            .Where(record =>
                record.Id == id && values.Contains(record.Value) && record.Value == value && record.Value == value
            );

        DatabaseProbeRecord first = await query.SingleAsync(cancellationToken);

        id = body.GetProperty("secondId").GetInt32();
        value = body.GetProperty("secondValue").GetString()!;
        values = body.GetProperty("secondValues").EnumerateArray().Select(item => item.GetString()!).ToArray();
        DatabaseProbeRecord second = await query.SingleAsync(cancellationToken);

        return JsonSerializer.SerializeToElement(
            new[] { DatabaseProbeState.From(first), DatabaseProbeState.From(second) },
            serializerOptions
        );
    }

    private static async Task<JsonElement> GetDatabaseValuesAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        _ = await database.Probes.SingleAsync(
            record => record.Id == body.GetProperty("ownId").GetInt32(),
            cancellationToken
        );
        var foreignEntry = database.Probes.Attach(
            new DatabaseProbeRecord { Id = body.GetProperty("foreignId").GetInt32() }
        );
        _ = await foreignEntry.GetDatabaseValuesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(new DatabaseProbeResponse(0), serializerOptions);
    }

    private static async Task<JsonElement> ChangeOwnerAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        DatabaseProbeRecord probe = await GetProbeAsync(database, body, cancellationToken);
        probe.CreateBy = body.GetProperty("createBy").GetString();
        database.Entry(probe).Property(record => record.CreateBy).IsModified = true;
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeState.From(probe), serializerOptions);
    }

    private static async Task<JsonElement> UpdateAttachedProbeAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        var probe = new DatabaseProbeRecord
        {
            Id = body.GetProperty("id").GetInt32(),
            CreateBy = body.GetProperty("createBy").GetString(),
        };
        database.Probes.Attach(probe);
        probe.Value = body.GetProperty("value").GetString()!;
        database.Entry(probe).Property(record => record.Value).IsModified = true;
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeState.From(probe), serializerOptions);
    }

    private static async Task<JsonElement> DeleteAttachedProbeAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        var probe = new DatabaseProbeRecord
        {
            Id = body.GetProperty("id").GetInt32(),
            CreateBy = body.GetProperty("createBy").GetString(),
        };
        database.Probes.Attach(probe);
        database.Probes.Remove(probe);
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeState.From(probe), serializerOptions);
    }

    private static async Task<JsonElement> AddChildAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        var child = CreateChild(body);
        database.Notes.Add(child);
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeNoteState.From(child), serializerOptions);
    }

    private static async Task<JsonElement> AddRootAndChildAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        int rootId = body.GetProperty("id").GetInt32();
        var root = new DatabaseProbeRecord { Id = rootId, Value = "root" };
        var child = new DatabaseProbeNote
        {
            Id = body.GetProperty("childId").GetInt32(),
            RootId = rootId,
            Value = "child",
        };
        database.Probes.Add(root);
        database.Notes.Add(child);
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(
            new DatabaseProbeOwnershipResponse(root.CreateBy, child.CreateBy),
            serializerOptions
        );
    }

    private static async Task<JsonElement> AddChildWithForgedRootAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        int rootId = body.GetProperty("rootId").GetInt32();
        database.Probes.Attach(
            new DatabaseProbeRecord { Id = rootId, CreateBy = body.GetProperty("createBy").GetString() }
        );
        var child = CreateChild(body);
        database.Notes.Add(child);
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeNoteState.From(child), serializerOptions);
    }

    private static async Task<JsonElement> DeleteRootAndChildAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        int rootId = body.GetProperty("id").GetInt32();
        DatabaseProbeRecord root = await database.Probes.SingleAsync(record => record.Id == rootId, cancellationToken);
        List<DatabaseProbeNote> children = await database
            .Notes.Where(note => note.RootId == rootId)
            .ToListAsync(cancellationToken);
        database.RemoveRange(children);
        database.Probes.Remove(root);
        await database.SaveChangesAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(new DatabaseProbeResponse(children.Count), serializerOptions);
    }

    private static async Task<JsonElement> RunUnsupportedOperationAsync(
        DatabaseTestContext database,
        JsonElement body,
        CancellationToken cancellationToken
    )
    {
        switch (body.GetProperty("operationIndex").GetInt32())
        {
            case 0:
                await database.Probes.IgnoreQueryFilters().ToListAsync(cancellationToken);
                break;
            case 1:
                await database.Probes.IgnoreQueryFilters(["ExtensionUserScope"]).ToListAsync(cancellationToken);
                break;
            case 2:
                await database.Probes.ExecuteUpdateAsync(
                    setters => setters.SetProperty(record => record.CreateBy, "other-user"),
                    cancellationToken
                );
                break;
            case 3:
                await database.Probes.ExecuteDeleteAsync(cancellationToken);
                break;
            case 4:
                await database
                    .Probes.FromSqlRaw(
                        "SELECT 1 AS id, 'value' AS value, NULL AS create_by, CURRENT_TIMESTAMP AS create_time, NULL AS update_by, NULL AS update_time"
                    )
                    .ToListAsync(cancellationToken);
                break;
            case 5:
                await database.Database.ExecuteSqlRawAsync("DELETE FROM setting", cancellationToken);
                break;
            case 6:
                await database.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToListAsync(cancellationToken);
                break;
            case 7:
            {
                await database.Database.OpenConnectionAsync(cancellationToken);
                DbConnection connection = database.Database.GetDbConnection();
                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
                await database.Database.UseTransactionAsync(transaction, cancellationToken);
                break;
            }
            default:
                throw new AgwException(ErrorCodes.InvalidParam);
        }

        return JsonSerializer.SerializeToElement(new DatabaseProbeResponse(0));
    }

    private static string? OptionalString(JsonElement body, string propertyName) =>
        body.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static async Task<JsonElement> BeginAndDisposeTransactionAsync(
        DatabaseTestContext database,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        await using (await database.Database.BeginTransactionAsync(cancellationToken)) { }
        return JsonSerializer.SerializeToElement(new DatabaseProbeResponse(0), serializerOptions);
    }

    private static async Task<JsonElement> WriteTransactionAsync(
        DatabaseTestContext database,
        JsonElement body,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var probe = new DatabaseProbeRecord
        {
            Id = body.GetProperty("id").GetInt32(),
            Value = body.GetProperty("value").GetString()!,
        };
        database.Probes.Add(probe);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(DatabaseProbeState.From(probe), serializerOptions);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadProbesAsync(
        DatabaseTestContext database,
        JsonSerializerOptions serializerOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (
            DatabaseProbeRecord probe in database
                .Probes.AsNoTracking()
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
        )
        {
            yield return JsonSerializer.SerializeToUtf8Bytes(DatabaseProbeState.From(probe), serializerOptions);
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> HoldWriteAsync(
        DatabaseTestContext database,
        JsonSerializerOptions serializerOptions,
        int id,
        string value,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var probe = new DatabaseProbeRecord { Id = id, Value = value };
        database.Probes.Add(probe);
        await database.SaveChangesAsync(cancellationToken);
        yield return JsonSerializer.SerializeToUtf8Bytes(DatabaseProbeState.From(probe), serializerOptions);
        await transaction.CommitAsync(cancellationToken);
        yield return JsonSerializer.SerializeToUtf8Bytes(new DatabaseProbeResponse(1), serializerOptions);
    }

    private static async Task<DatabaseProbeRecord> GetProbeAsync(
        DatabaseTestContext database,
        JsonElement body,
        CancellationToken cancellationToken
    ) => await database.Probes.SingleAsync(record => record.Id == body.GetProperty("id").GetInt32(), cancellationToken);

    private static DatabaseProbeNote CreateChild(JsonElement body) =>
        new()
        {
            Id = body.GetProperty("childId").GetInt32(),
            RootId = body.GetProperty("rootId").GetInt32(),
            Value = body.GetProperty("value").GetString()!,
        };

    private static JsonElement RequireBody(ExtensionEndpointRequest request) =>
        request.Body ?? throw new AgwException(ErrorCodes.InvalidParam);

    public sealed class DatabaseEventHandler : ExtensionEventHandlerBase<DatabaseTestContext>
    {
        protected override ValueTask<ExtensionEventHandlerResult> HandleEventAsync(
            DatabaseTestContext dbContext,
            IExtensionContext context,
            ExtensionEventDelivery delivery,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delivery.Event.Data?.Json is not JsonElement data)
            {
                throw new AgwException(ErrorCodes.InvalidParam);
            }

            int id = data.GetProperty("id").GetInt32();
            string value = data.GetProperty("value").GetString()!;
            dbContext.Probes.Add(new DatabaseProbeRecord { Id = id, Value = value });
            ExtensionOutbox.Enqueue(
                dbContext,
                ExtensionOutboxKind.PublishEvent,
                $"probe:{id}",
                new CloudEventDraft { Id = $"probe:{id}", Type = "database.probe.created" },
                context.JsonOptions
            );

            bool retry = data.TryGetProperty("retry", out JsonElement retryValue) && retryValue.GetBoolean();
            return ValueTask.FromResult(
                retry
                    ? ExtensionEventHandlerResult.Retry(TimeSpan.FromSeconds(1), "Retry requested by test event.")
                    : ExtensionEventHandlerResult.Succeeded
            );
        }
    }

    private sealed class DatabaseTestException(string message) : Exception(message);
}

public sealed class DatabaseTestContext(
    DbContextOptions<DatabaseTestContext> options,
    IExtensionDatabaseIdentity identity,
    IExtensionUserContext userContext,
    TimeProvider timeProvider
) : ExtensionDbContext(options, identity, userContext, timeProvider)
{
    public DbSet<DatabaseProbeRecord> Probes => Set<DatabaseProbeRecord>();

    public DbSet<DatabaseProbeNote> Notes => Set<DatabaseProbeNote>();

    public DbSet<DatabaseOwnedProbeRecord> OwnedProbes => Set<DatabaseOwnedProbeRecord>();

    protected override void ConfigureExtensionModel(ModelBuilder modelBuilder)
    {
        string timeType =
            Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "timestamp with time zone";
        modelBuilder.Entity<DatabaseProbeRecord>(entity =>
        {
            entity.ToTable(DatabaseTestSchema.TableName);
            entity.HasKey(record => record.Id).HasName($"pk_{DatabaseTestSchema.TableName}");
            entity.Property(record => record.Value).HasColumnType("text");
            entity.Property(record => record.CreateBy).HasColumnType("text");
            entity.Property(record => record.CreateTime).HasColumnType(timeType);
            entity.Property(record => record.UpdateBy).HasColumnType("text");
            entity.Property(record => record.UpdateTime).HasColumnType(timeType);
        });
        modelBuilder.Entity<DatabaseProbeNote>(entity =>
        {
            entity.ToTable(DatabaseTestSchema.NoteTableName);
            entity.HasKey(note => note.Id).HasName($"pk_{DatabaseTestSchema.NoteTableName}");
            entity.Property(note => note.Value).HasColumnType("text");
            entity.Property(note => note.CreateBy).HasColumnType("text");
            entity.Property(note => note.CreateTime).HasColumnType(timeType);
            entity.Property(note => note.UpdateBy).HasColumnType("text");
            entity.Property(note => note.UpdateTime).HasColumnType(timeType);
        });
        modelBuilder.Entity<DatabaseOwnedProbeRecord>(entity =>
        {
            entity.ToTable(DatabaseTestSchema.OwnedProbeTableName);
            entity.HasKey(record => record.Id).HasName($"pk_{DatabaseTestSchema.OwnedProbeTableName}");
            entity.Property(record => record.Value).HasColumnType("text");
            entity.Property(record => record.CreateBy).HasColumnType("text");
            entity.Property(record => record.CreateTime).HasColumnType(timeType);
            entity.Property(record => record.UpdateBy).HasColumnType("text");
            entity.Property(record => record.UpdateTime).HasColumnType(timeType);
        });
    }
}

public sealed class DatabaseProbeRecord : IExtensionOwnedEntity
{
    public int Id { get; set; }

    public string Value { get; set; } = string.Empty;

    public string? CreateBy { get; set; }

    public DateTimeOffset CreateTime { get; set; }

    public string? UpdateBy { get; set; }

    public DateTimeOffset? UpdateTime { get; set; }
}

public sealed class DatabaseProbeNote : IExtensionOwnedChild<DatabaseProbeRecord, int>
{
    public int Id { get; set; }

    public int RootId { get; set; }

    public string Value { get; set; } = string.Empty;

    public string? CreateBy { get; set; }

    public DateTimeOffset CreateTime { get; set; }

    public string? UpdateBy { get; set; }

    public DateTimeOffset? UpdateTime { get; set; }
}

public sealed class DatabaseOwnedProbeRecord : IExtensionOwnedEntity
{
    public Guid Id { get; set; }

    public string Value { get; set; } = string.Empty;

    public string? CreateBy { get; set; }

    public DateTimeOffset CreateTime { get; set; }

    public string? UpdateBy { get; set; }

    public DateTimeOffset? UpdateTime { get; set; }
}

public sealed record OwnedRecordResponse(Guid Id, string? CreateBy, string Value);

public sealed record DatabaseInvocationResponse(Guid? InvocationId, string? UserId);

public sealed record DatabaseModelResponse(
    string TableName,
    string[] ColumnNames,
    int ForeignKeyCount,
    string[] MigrationIds
);

public sealed record DatabaseProbeState(
    int Id,
    string Value,
    string? CreateBy,
    DateTimeOffset CreateTime,
    string? UpdateBy,
    DateTimeOffset? UpdateTime
)
{
    public static DatabaseProbeState From(DatabaseProbeRecord record) =>
        new(record.Id, record.Value, record.CreateBy, record.CreateTime, record.UpdateBy, record.UpdateTime);
}

public sealed record DatabaseProbeNoteState(
    int Id,
    int RootId,
    string Value,
    string? CreateBy,
    DateTimeOffset CreateTime,
    string? UpdateBy,
    DateTimeOffset? UpdateTime
)
{
    public static DatabaseProbeNoteState From(DatabaseProbeNote note) =>
        new(note.Id, note.RootId, note.Value, note.CreateBy, note.CreateTime, note.UpdateBy, note.UpdateTime);
}

public sealed record DatabaseProbeOwnershipResponse(string? RootOwner, string? ChildOwner);

public static class DatabaseTestSchema
{
    public const string ExtensionId = "019a4c175f007a808b3d2b732f48a100";
    public const string TablePrefix = $"zext_{ExtensionId}_";
    public const string TableName = $"zext_{ExtensionId}_database_probe";
    public const string NoteTableName = $"zext_{ExtensionId}_database_probe_note";
    public const string OwnedProbeTableName = $"zext_{ExtensionId}_database_owned_probe";
    public const string InboxTableName = $"{TablePrefix}__inbox";
    public const string OutboxTableName = $"{TablePrefix}__outbox";
    public const string OutboxIdempotencyIndexName = $"{TablePrefix}uq_outbox_key";
    public const string HistoryTableName = $"zext_{ExtensionId}___ef_migrations_history";
}

public sealed class DatabaseTestMigrationMarker { }

[DbContext(typeof(DatabaseTestContext))]
[Migration("20261107000100_Initial")]
public sealed class DatabaseTestInitialMigration : Migration
{
    private OperationBuilder<AddColumnOperation> MessagingTimestamp(ColumnsBuilder table, bool nullable) =>
        ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite"
            ? table.Column<long>(type: "INTEGER", nullable: nullable)
            : table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: nullable);

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        string timeType =
            ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "timestamp with time zone";
        string guidType = ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "uuid";
        migrationBuilder.CreateTable(
            name: DatabaseTestSchema.TableName,
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                value = table.Column<string>(type: "text", nullable: false),
                create_by = table.Column<string>(type: "text", nullable: true),
                create_time = table.Column<DateTimeOffset>(type: timeType, nullable: false),
                update_by = table.Column<string>(type: "text", nullable: true),
                update_time = table.Column<DateTimeOffset>(type: timeType, nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{DatabaseTestSchema.TableName}", row => row.id)
        );
        migrationBuilder.CreateTable(
            name: DatabaseTestSchema.NoteTableName,
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                root_id = table.Column<int>(type: "integer", nullable: false),
                value = table.Column<string>(type: "text", nullable: false),
                create_by = table.Column<string>(type: "text", nullable: true),
                create_time = table.Column<DateTimeOffset>(type: timeType, nullable: false),
                update_by = table.Column<string>(type: "text", nullable: true),
                update_time = table.Column<DateTimeOffset>(type: timeType, nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{DatabaseTestSchema.NoteTableName}", row => row.id)
        );
        migrationBuilder.CreateTable(
            name: DatabaseTestSchema.OwnedProbeTableName,
            columns: table => new
            {
                id = table.Column<Guid>(type: guidType, nullable: false),
                value = table.Column<string>(type: "text", nullable: false),
                create_by = table.Column<string>(type: "text", nullable: true),
                create_time = table.Column<DateTimeOffset>(type: timeType, nullable: false),
                update_by = table.Column<string>(type: "text", nullable: true),
                update_time = table.Column<DateTimeOffset>(type: timeType, nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{DatabaseTestSchema.OwnedProbeTableName}", row => row.id)
        );
        migrationBuilder.CreateTable(
            name: DatabaseTestSchema.InboxTableName,
            columns: table => new
            {
                delivery_id = table.Column<Guid>(type: guidType, nullable: false),
                create_by = table.Column<string>(type: "text", nullable: false),
                create_time = MessagingTimestamp(table, false),
                update_by = table.Column<string>(type: "text", nullable: true),
                update_time = MessagingTimestamp(table, true),
                processed_at = MessagingTimestamp(table, false),
                result_json = table.Column<string>(type: "text", nullable: false),
            },
            constraints: table => table.PrimaryKey($"pk_{DatabaseTestSchema.InboxTableName}", row => row.delivery_id)
        );
        migrationBuilder.CreateTable(
            name: DatabaseTestSchema.OutboxTableName,
            columns: table => new
            {
                id = table.Column<Guid>(type: guidType, nullable: false),
                create_by = table.Column<string>(type: "text", nullable: false),
                create_time = MessagingTimestamp(table, false),
                update_by = table.Column<string>(type: "text", nullable: true),
                update_time = MessagingTimestamp(table, true),
                kind = table.Column<string>(type: "text", nullable: false),
                idempotency_key = table.Column<string>(type: "text", nullable: false),
                payload_json = table.Column<string>(type: "text", nullable: false),
                state = table.Column<string>(type: "text", nullable: false),
                result_json = table.Column<string>(type: "text", nullable: true),
                error_code = table.Column<int>(type: "integer", nullable: true),
                attempt = table.Column<int>(type: "integer", nullable: false),
                next_attempt_at = MessagingTimestamp(table, true),
                completed_at = MessagingTimestamp(table, true),
                last_error = table.Column<string>(type: "text", nullable: true),
            },
            constraints: table => table.PrimaryKey($"pk_{DatabaseTestSchema.OutboxTableName}", row => row.id)
        );
        migrationBuilder.CreateIndex(
            name: DatabaseTestSchema.OutboxIdempotencyIndexName,
            table: DatabaseTestSchema.OutboxTableName,
            column: "idempotency_key",
            unique: true
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(DatabaseTestSchema.OutboxTableName);
        migrationBuilder.DropTable(DatabaseTestSchema.InboxTableName);
        migrationBuilder.DropTable(DatabaseTestSchema.OwnedProbeTableName);
        migrationBuilder.DropTable(DatabaseTestSchema.NoteTableName);
        migrationBuilder.DropTable(DatabaseTestSchema.TableName);
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        string timeType =
            ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "timestamp with time zone";
        modelBuilder.UseExtensionMessaging(DatabaseTestSchema.TablePrefix, timeType);
        modelBuilder.Entity<DatabaseProbeRecord>(entity =>
        {
            entity.ToTable(DatabaseTestSchema.TableName);
            entity.HasKey(record => record.Id).HasName($"pk_{DatabaseTestSchema.TableName}");
            entity.Property(record => record.Id).HasColumnName("id").HasColumnType("integer");
            entity.Property(record => record.Value).HasColumnName("value").HasColumnType("text");
            entity.Property(record => record.CreateBy).HasColumnName("create_by").HasColumnType("text");
            entity.Property(record => record.CreateTime).HasColumnName("create_time").HasColumnType(timeType);
            entity.Property(record => record.UpdateBy).HasColumnName("update_by").HasColumnType("text");
            entity.Property(record => record.UpdateTime).HasColumnName("update_time").HasColumnType(timeType);
        });
        modelBuilder.Entity<DatabaseProbeNote>(entity =>
        {
            entity.ToTable(DatabaseTestSchema.NoteTableName);
            entity.HasKey(note => note.Id).HasName($"pk_{DatabaseTestSchema.NoteTableName}");
            entity.Property(note => note.Id).HasColumnName("id").HasColumnType("integer");
            entity.Property(note => note.RootId).HasColumnName("root_id").HasColumnType("integer");
            entity.Property(note => note.Value).HasColumnName("value").HasColumnType("text");
            entity.Property(note => note.CreateBy).HasColumnName("create_by").HasColumnType("text");
            entity.Property(note => note.CreateTime).HasColumnName("create_time").HasColumnType(timeType);
            entity.Property(note => note.UpdateBy).HasColumnName("update_by").HasColumnType("text");
            entity.Property(note => note.UpdateTime).HasColumnName("update_time").HasColumnType(timeType);
        });
        modelBuilder.Entity<DatabaseOwnedProbeRecord>(entity =>
        {
            entity.ToTable(DatabaseTestSchema.OwnedProbeTableName);
            entity.HasKey(record => record.Id).HasName($"pk_{DatabaseTestSchema.OwnedProbeTableName}");
            entity
                .Property(record => record.Id)
                .HasColumnName("id")
                .HasColumnType(ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "uuid");
            entity.Property(record => record.Value).HasColumnName("value").HasColumnType("text");
            entity.Property(record => record.CreateBy).HasColumnName("create_by").HasColumnType("text");
            entity.Property(record => record.CreateTime).HasColumnName("create_time").HasColumnType(timeType);
            entity.Property(record => record.UpdateBy).HasColumnName("update_by").HasColumnType("text");
            entity.Property(record => record.UpdateTime).HasColumnName("update_time").HasColumnType(timeType);
        });
    }
}
