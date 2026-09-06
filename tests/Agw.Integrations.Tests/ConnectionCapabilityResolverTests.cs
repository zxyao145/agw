using System.Security.Claims;
using System.Text.Json;
using Agw.Infrastructure.Data;
using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Plugins;
using Agw.Integrations.Domain.Plugins;
using Agw.Integrations.Mcp;
using Agw.Integrations.Tools.GitHub;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using IntegrationConnection = Agw.Shared.Data.Entities.Integrations.Connection;

namespace Agw.Integrations.Tests;

public class ConnectionCapabilityResolverTests : IDisposable
{
    private readonly IDisposable _userScope = UserInfoUtil.Push(
        new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test")], authenticationType: "Test")
        )
    );

    public void Dispose() => _userScope.Dispose();

    [Fact]
    public async Task Resolve_TwoReadyGitHubConnections_CreatesAliasToolsUsingExactConnectionAndRotatedToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ResolverTestScope.CreateAsync(cancellationToken);
        var work = await scope.AddReadyConnectionAsync("work", cancellationToken);
        var personal = await scope.AddReadyConnectionAsync("personal", cancellationToken);

        await using var resolution = await scope.Resolver.ResolveAsync(
            Guid.CreateVersion7(),
            [work.Id, personal.Id],
            cancellationToken
        );

        Assert.Contains(resolution.NativeTools, tool => tool.Name == "work__current_user");
        Assert.Contains(resolution.NativeTools, tool => tool.Name == "personal__current_user");
        Assert.All(
            resolution.Tools,
            tool => Assert.DoesNotContain("token", tool.Description, StringComparison.OrdinalIgnoreCase)
        );

        scope.Invocations.Tokens[work.Id] = "work-token-v1";
        var workTool = Assert.IsAssignableFrom<AIFunction>(
            resolution.Tools.Single(tool => tool.Name == "work__current_user")
        );
        var firstUser = Assert.IsType<JsonElement>(await workTool.InvokeAsync(cancellationToken: cancellationToken));
        Assert.Equal("work-token-v1", firstUser.GetProperty("login").GetString());

        scope.Invocations.Tokens[work.Id] = "work-token-v2";
        var secondUser = Assert.IsType<JsonElement>(await workTool.InvokeAsync(cancellationToken: cancellationToken));
        Assert.Equal("work-token-v2", secondUser.GetProperty("login").GetString());
        Assert.Equal([work.Id, work.Id], scope.Invocations.ConnectionIds);
    }

    [Fact]
    public async Task Resolve_ForeignConnection_ReturnsNotFoundWarningWithoutTools()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ResolverTestScope.CreateAsync(cancellationToken, userId: "other-user");
        var connection = await scope.AddReadyConnectionAsync("work", cancellationToken);

        await using var resolution = await scope.Resolver.ResolveAsync(
            Guid.CreateVersion7(),
            [connection.Id],
            cancellationToken
        );

        Assert.Empty(resolution.Tools);
        var warning = Assert.Single(resolution.Warnings);
        Assert.Equal(ConnectionCapabilityWarningCodes.ConnectionNotFound, warning.Code);
        Assert.Equal("The integration was not found.", warning.Message);
    }

    [Fact]
    public async Task Resolve_DuplicateConnectionIds_DeduplicatesToolsAndPluginSkill()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ResolverTestScope.CreateAsync(cancellationToken);
        var work = await scope.AddReadyConnectionAsync("work", cancellationToken);
        var personal = await scope.AddReadyConnectionAsync("personal", cancellationToken);

        await using var resolution = await scope.Resolver.ResolveAsync(
            Guid.CreateVersion7(),
            [work.Id, work.Id, personal.Id],
            cancellationToken
        );

        Assert.Equal(6, resolution.NativeTools.Count);
        Assert.Single(resolution.PluginSkills);
        Assert.Equal("github", resolution.PluginSkills[0].SkillId);
        Assert.EndsWith("SKILL.md", resolution.PluginSkills[0].SkillFilePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConnectionStatus.NeedsConfiguration, ConnectionCapabilityWarningCodes.ConnectionNeedsConfiguration)]
    [InlineData(ConnectionStatus.PendingAuthorization, ConnectionCapabilityWarningCodes.ConnectionPendingAuthorization)]
    [InlineData(ConnectionStatus.Unverified, ConnectionCapabilityWarningCodes.ConnectionUnverified)]
    [InlineData(ConnectionStatus.Expired, ConnectionCapabilityWarningCodes.ConnectionExpired)]
    [InlineData(ConnectionStatus.Invalid, ConnectionCapabilityWarningCodes.ConnectionInvalid)]
    [InlineData(ConnectionStatus.Disabled, ConnectionCapabilityWarningCodes.ConnectionDisabled)]
    [InlineData(ConnectionStatus.DefinitionUnavailable, ConnectionCapabilityWarningCodes.DefinitionUnavailable)]
    public async Task Resolve_NonReadyConnection_SkipsWholeConnectionWithStableWarning(
        ConnectionStatus status,
        string expectedCode
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ResolverTestScope.CreateAsync(cancellationToken);
        var connection = await scope.AddConnectionAsync("work", status, enabled: true, cancellationToken);

        await using var resolution = await scope.Resolver.ResolveAsync(
            Guid.CreateVersion7(),
            [connection.Id],
            cancellationToken
        );

        Assert.Empty(resolution.Tools);
        var warning = Assert.Single(resolution.Warnings);
        Assert.Equal(expectedCode, warning.Code);
        Assert.Equal(connection.Id, warning.ConnectionId);
    }

    [Fact]
    public async Task Resolve_ReadyConnectionWithExpiredOrUnreadableCredential_SkipsConnectionWithoutSecret()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);
        await using var scope = await ResolverTestScope.CreateAsync(cancellationToken, now);
        var expired = await scope.AddReadyConnectionAsync("expired", cancellationToken);
        scope.Credentials.Values[(expired.Id, "oauth.access-token")] = new ResolvedCredential
        {
            Value = "never-expose-expired-secret",
            ExpiresAtUtc = now.AddMinutes(-1),
        };
        var missing = await scope.AddReadyConnectionAsync("missing", cancellationToken);
        scope.Credentials.Values.Remove((missing.Id, "oauth.access-token"));

        await using var resolution = await scope.Resolver.ResolveAsync(
            Guid.CreateVersion7(),
            [expired.Id, missing.Id],
            cancellationToken
        );

        Assert.Empty(resolution.Tools);
        Assert.Contains(
            resolution.Warnings,
            warning =>
                warning.ConnectionId == expired.Id && warning.Code == ConnectionCapabilityWarningCodes.ConnectionExpired
        );
        Assert.Contains(
            resolution.Warnings,
            warning =>
                warning.ConnectionId == missing.Id
                && warning.Code == ConnectionCapabilityWarningCodes.CredentialUnavailable
        );
        Assert.DoesNotContain(
            "never-expose-expired-secret",
            string.Join('|', resolution.Warnings.Select(item => item.Message)),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Resolve_CredentialReadCancellation_PropagatesCancellation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await ResolverTestScope.CreateAsync(cancellationToken);
        var connection = await scope.AddReadyConnectionAsync("work", cancellationToken);
        scope.Credentials.ReadException = new OperationCanceledException(cancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Resolver.ResolveAsync(Guid.CreateVersion7(), [connection.Id], cancellationToken)
        );
    }

    [Fact]
    public async Task Resolve_McpBinding_InjectsOnlyDeclaredCredentialAndNamespacesTool()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var materializer = new TrackingMcpToolMaterializer("search");
        await using var scope = await ResolverTestScope.CreateAsync(
            cancellationToken,
            catalog: new McpTestCatalog(sourceCount: 1),
            mcpToolMaterializer: materializer
        );
        var connection = await scope.AddConnectionAsync(
            "docs",
            ConnectionStatus.Ready,
            enabled: true,
            cancellationToken,
            pluginId: "mcp-test",
            connectorId: "remote",
            authSchemeId: "oauth2"
        );
        scope.Credentials.Values[(connection.Id, "oauth.access-token")] = new ResolvedCredential
        {
            Value = "mcp-secret",
        };

        await using var resolution = await scope.Resolver.ResolveAsync(
            Guid.CreateVersion7(),
            [connection.Id],
            cancellationToken
        );

        Assert.Equal("docs__search", Assert.Single(resolution.McpTools).Name);
        var descriptor = Assert.IsType<McpHttpEndpointDescriptor>(Assert.Single(materializer.Descriptors));
        Assert.Empty(descriptor.Headers);
        Assert.Equal("Bearer mcp-secret", descriptor.CredentialHeaders["Authorization"]);
        Assert.DoesNotContain("X-Other-Auth", descriptor.CredentialHeaders.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "mcp-secret",
            Assert.Single(resolution.McpSources).ToolNames[0],
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task Resolve_McpToolAfterCredentialRotation_UsesLatestCredentialForInvocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var materializer = new TrackingMcpToolMaterializer("search");
        await using var scope = await ResolverTestScope.CreateAsync(
            cancellationToken,
            catalog: new McpTestCatalog(sourceCount: 1),
            mcpToolMaterializer: materializer
        );
        var connection = await scope.AddConnectionAsync(
            "docs",
            ConnectionStatus.Ready,
            enabled: true,
            cancellationToken,
            pluginId: "mcp-test",
            connectorId: "remote",
            authSchemeId: "oauth2"
        );
        scope.Credentials.Values[(connection.Id, "oauth.access-token")] = new ResolvedCredential
        {
            Value = "mcp-secret-v1",
        };

        await using var resolution = await scope.Resolver.ResolveAsync(
            Guid.CreateVersion7(),
            [connection.Id],
            cancellationToken
        );
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(resolution.McpTools));

        scope.Credentials.Values[(connection.Id, "oauth.access-token")] = new ResolvedCredential
        {
            Value = "mcp-secret-v2",
        };
        await tool.InvokeAsync(cancellationToken: cancellationToken);

        Assert.Equal(2, materializer.Descriptors.Count);
        var invocationDescriptor = Assert.IsType<McpHttpEndpointDescriptor>(materializer.Descriptors[^1]);
        Assert.Equal("Bearer mcp-secret-v2", invocationDescriptor.CredentialHeaders["Authorization"]);
        Assert.All(materializer.Resources, resource => Assert.Equal(1, resource.DisposeCount));
    }

    [Fact]
    public async Task Resolve_ReadyConnectionWithUnreadableDeclaredMcpCredential_SkipsWholeConnectionWithWarning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var materializer = new TrackingMcpToolMaterializer("search");
        await using var scope = await ResolverTestScope.CreateAsync(
            cancellationToken,
            catalog: new McpTestCatalog(sourceCount: 1, useMissingConnectionBinding: true),
            mcpToolMaterializer: materializer
        );
        var connection = await scope.AddConnectionAsync(
            "docs",
            ConnectionStatus.Ready,
            enabled: true,
            cancellationToken,
            pluginId: "mcp-test",
            connectorId: "remote",
            authSchemeId: "oauth2"
        );
        scope.Credentials.Values[(connection.Id, "oauth.access-token")] = new ResolvedCredential
        {
            Value = "oauth-secret",
        };

        await using var resolution = await scope.Resolver.ResolveAsync(
            Guid.CreateVersion7(),
            [connection.Id],
            cancellationToken
        );

        Assert.Empty(resolution.Tools);
        Assert.Empty(materializer.Descriptors);
        Assert.Contains(
            resolution.Warnings,
            warning => warning.Code == ConnectionCapabilityWarningCodes.CredentialUnavailable
        );
    }

    [Fact]
    public async Task Resolve_CrossSourceToolConflict_DisposesCreatedMcpLeasesAndThrowsExplicitError()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var materializer = new TrackingMcpToolMaterializer("same");
        await using var scope = await ResolverTestScope.CreateAsync(
            cancellationToken,
            catalog: new McpTestCatalog(sourceCount: 2),
            mcpToolMaterializer: materializer
        );
        var connection = await scope.AddConnectionAsync(
            "docs",
            ConnectionStatus.Ready,
            enabled: true,
            cancellationToken,
            pluginId: "mcp-test",
            connectorId: "remote",
            authSchemeId: "oauth2"
        );
        scope.Credentials.Values[(connection.Id, "oauth.access-token")] = new ResolvedCredential { Value = "secret" };

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            scope.Resolver.ResolveAsync(Guid.CreateVersion7(), [connection.Id], cancellationToken)
        );

        Assert.Equal(ErrorCodes.IntegrationToolNameConflict.Code, error.Code);
        Assert.All(materializer.Resources, resource => Assert.Equal(1, resource.DisposeCount));
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_PluginSkillTraversalOrMissingFile_SkipsWithWarning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("agw-plugin-root-").FullName;
        try
        {
            await using var scope = await ResolverTestScope.CreateAsync(
                cancellationToken,
                catalog: new UnsafeSkillCatalog(),
                pluginContentRoot: root
            );
            var connection = await scope.AddConnectionAsync(
                "unsafe",
                ConnectionStatus.Ready,
                enabled: true,
                cancellationToken,
                pluginId: "unsafe-plugin",
                connectorId: "connector",
                authSchemeId: "api-key"
            );

            await using var resolution = await scope.Resolver.ResolveAsync(
                Guid.CreateVersion7(),
                [connection.Id],
                cancellationToken
            );

            Assert.Empty(resolution.PluginSkills);
            Assert.Contains(
                resolution.Warnings,
                warning => warning.Code == ConnectionCapabilityWarningCodes.PluginSkillUnavailable
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_PluginSkillThroughSymlinkOutsideRoot_SkipsWithWarning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("agw-plugin-root-").FullName;
        var outside = Directory.CreateTempSubdirectory("agw-plugin-outside-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "SKILL.md"), "outside", cancellationToken);
            Directory.CreateDirectory(Path.Combine(root, "skills"));
            Directory.CreateSymbolicLink(Path.Combine(root, "skills", "linked"), outside);
            await using var scope = await ResolverTestScope.CreateAsync(
                cancellationToken,
                catalog: new UnsafeSkillCatalog("skills/linked/SKILL.md"),
                pluginContentRoot: root
            );
            var connection = await scope.AddConnectionAsync(
                "unsafe",
                ConnectionStatus.Ready,
                enabled: true,
                cancellationToken,
                pluginId: "unsafe-plugin",
                connectorId: "connector",
                authSchemeId: "api-key"
            );

            await using var resolution = await scope.Resolver.ResolveAsync(
                Guid.CreateVersion7(),
                [connection.Id],
                cancellationToken
            );

            Assert.Empty(resolution.PluginSkills);
            Assert.Contains(
                resolution.Warnings,
                warning => warning.Code == ConnectionCapabilityWarningCodes.PluginSkillUnavailable
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private sealed class ResolverTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _sqlite;

        private ResolverTestScope(
            SqliteConnection sqlite,
            AgwDbContext dbContext,
            ConnectionCapabilityResolver resolver,
            MutableCredentialReader credentials,
            TrackingGitHubInvoker invocations
        )
        {
            _sqlite = sqlite;
            DbContext = dbContext;
            Resolver = resolver;
            Credentials = credentials;
            Invocations = invocations;
        }

        public AgwDbContext DbContext { get; }
        public ConnectionCapabilityResolver Resolver { get; }
        public MutableCredentialReader Credentials { get; }
        public TrackingGitHubInvoker Invocations { get; }

        public static async Task<ResolverTestScope> CreateAsync(
            CancellationToken cancellationToken,
            DateTimeOffset? now = null,
            IPluginCatalog? catalog = null,
            IMcpToolMaterializer? mcpToolMaterializer = null,
            string? pluginContentRoot = null,
            string userId = "test"
        )
        {
            var sqlite = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            await sqlite.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(sqlite)
                .UseSnakeCaseNamingConvention()
                .Options;
            var dbContext = new AgwDbContext(options);
            await dbContext.Database.EnsureCreatedAsync(cancellationToken);

            var invocations = new TrackingGitHubInvoker();
            var services = new ServiceCollection();
            services.AddScoped<IGitHubConnectionInvoker>(_ => invocations);
            var provider = services.BuildServiceProvider();
            var nativeProvider = new GitHubConnectionNativeCapabilityProvider(
                provider.GetRequiredService<IServiceScopeFactory>()
            );

            var credentials = new MutableCredentialReader();
            var mcpToolInvoker = new ForwardingMcpToolInvoker();
            var resolver = new ConnectionCapabilityResolver(
                dbContext,
                catalog ?? new TestCatalog(),
                credentials,
                [nativeProvider],
                mcpToolMaterializer ?? new EmptyMcpToolMaterializer(),
                mcpToolInvoker,
                new PluginSkillMetadataReader(
                    new FixedPluginContentRootProvider(pluginContentRoot ?? AppContext.BaseDirectory)
                ),
                new FixedTimeProvider(now ?? TimeProvider.System.GetUtcNow()),
                new TestUserInfoService(userId)
            );
            mcpToolInvoker.Resolver = resolver;
            return new ResolverTestScope(sqlite, dbContext, resolver, credentials, invocations);
        }

        public Task<IntegrationConnection> AddReadyConnectionAsync(string alias, CancellationToken cancellationToken)
        {
            return AddConnectionAsync(alias, ConnectionStatus.Ready, enabled: true, cancellationToken);
        }

        public async Task<IntegrationConnection> AddConnectionAsync(
            string alias,
            ConnectionStatus status,
            bool enabled,
            CancellationToken cancellationToken,
            string pluginId = "github",
            string connectorId = "github-cloud",
            string authSchemeId = "oauth2"
        )
        {
            var connection = new IntegrationConnection
            {
                Id = Guid.CreateVersion7(),
                PluginId = pluginId,
                ConnectorId = connectorId,
                AuthSchemeId = authSchemeId,
                Alias = alias,
                DisplayName = alias,
                Status = status,
                Enabled = enabled,
                CreateBy = "test",
                CreateTime = TimeProvider.System.GetUtcNow(),
            };
            DbContext.Connections.Add(connection);
            if (!await DbContext.PluginInstallations.AnyAsync(item => item.PluginId == pluginId, cancellationToken))
            {
                DbContext.PluginInstallations.Add(
                    new PluginInstallation
                    {
                        Id = Guid.CreateVersion7(),
                        PluginId = pluginId,
                        Enabled = true,
                        CreateBy = "test",
                        CreateTime = TimeProvider.System.GetUtcNow(),
                    }
                );
            }
            await DbContext.SaveChangesAsync(cancellationToken);
            Credentials.Values[(connection.Id, "oauth.access-token")] = new ResolvedCredential
            {
                Value = $"{alias}-token",
            };
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _sqlite.DisposeAsync();
        }
    }

    private sealed class ForwardingMcpToolInvoker : IConnectionMcpToolInvoker
    {
        public ConnectionCapabilityResolver Resolver { get; set; } = null!;

        public ValueTask<object?> InvokeAsync(
            Guid connectionId,
            string sourceId,
            string operationName,
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            return Resolver.InvokeMcpToolAsync(connectionId, sourceId, operationName, arguments, cancellationToken);
        }
    }

    private sealed class MutableCredentialReader : IConnectionCredentialReader
    {
        public Dictionary<(Guid Id, string Slot), ResolvedCredential> Values { get; } = [];

        public Exception? ReadException { get; set; }

        public Task<ResolvedCredential?> ReadConnectionAsync(
            Guid connectionId,
            string slot,
            CancellationToken cancellationToken
        )
        {
            if (ReadException != null)
            {
                return Task.FromException<ResolvedCredential?>(ReadException);
            }

            Values.TryGetValue((connectionId, slot), out var value);
            return Task.FromResult(value);
        }

        public Task<ResolvedCredential?> ReadPluginInstallationAsync(
            Guid pluginInstallationId,
            string slot,
            CancellationToken cancellationToken
        )
        {
            if (ReadException != null)
            {
                return Task.FromException<ResolvedCredential?>(ReadException);
            }

            Values.TryGetValue((pluginInstallationId, slot), out var value);
            return Task.FromResult(value);
        }
    }

    private sealed class TrackingGitHubInvoker : IGitHubConnectionInvoker
    {
        public Dictionary<Guid, string> Tokens { get; } = [];
        public List<Guid> ConnectionIds { get; } = [];

        public Task<GitHubUserInfo> GetCurrentUserAsync(Guid connectionId, CancellationToken cancellationToken)
        {
            ConnectionIds.Add(connectionId);
            return Task.FromResult(new GitHubUserInfo { Login = Tokens[connectionId] });
        }

        public Task<IReadOnlyList<Tools.GitHub.Dtos.GitHubRepoInfo>> ListRepositoriesAsync(
            Guid connectionId,
            CancellationToken cancellationToken
        )
        {
            ConnectionIds.Add(connectionId);
            return Task.FromResult<IReadOnlyList<Tools.GitHub.Dtos.GitHubRepoInfo>>([]);
        }

        public Task<Tools.GitHub.Dtos.CloneResult> CloneRepositoryAsync(
            Guid connectionId,
            Guid projectId,
            string owner,
            string repository,
            string? relativePath,
            CancellationToken cancellationToken
        )
        {
            ConnectionIds.Add(connectionId);
            return Task.FromResult(new Tools.GitHub.Dtos.CloneResult(true, null, null, null));
        }
    }

    private sealed class EmptyMcpToolMaterializer : IMcpToolMaterializer
    {
        public Task<ConnectionToolLease> MaterializeAsync(
            McpEndpointDescriptor descriptor,
            McpRuntimeOverrides? runtimeOverrides = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(new ConnectionToolLease([], []));
        }
    }

    private sealed class TrackingMcpToolMaterializer : IMcpToolMaterializer
    {
        private readonly string _toolName;

        public TrackingMcpToolMaterializer(string toolName)
        {
            _toolName = toolName;
        }

        public List<McpEndpointDescriptor> Descriptors { get; } = [];
        public List<TrackingResource> Resources { get; } = [];

        public Task<ConnectionToolLease> MaterializeAsync(
            McpEndpointDescriptor descriptor,
            McpRuntimeOverrides? runtimeOverrides = null,
            CancellationToken cancellationToken = default
        )
        {
            Descriptors.Add(descriptor);
            var resource = new TrackingResource();
            Resources.Add(resource);
            AITool tool = AIFunctionFactory.Create(
                (Func<string>)(() => "ok"),
                new AIFunctionFactoryOptions { Name = _toolName }
            );
            return Task.FromResult(new ConnectionToolLease([tool], [resource]));
        }
    }

    private sealed class TrackingResource : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestCatalog : IPluginCatalog
    {
        private readonly PluginDefinition _plugin = new()
        {
            Id = "github",
            Version = "1.0.0",
            DisplayName = "GitHub",
            Connectors =
            [
                new ConnectorDefinition
                {
                    Id = "github-cloud",
                    DisplayName = "GitHub",
                    AuthSchemes =
                    [
                        new AuthSchemeDefinition
                        {
                            Id = "oauth2",
                            DisplayName = "OAuth",
                            Type = AuthSchemeType.OAuth2,
                            OAuth2AuthorizationCode = new OAuth2AuthorizationCodeSettings
                            {
                                AuthorizationEndpoint = "https://example.test/authorize",
                                TokenEndpoint = "https://example.test/token",
                                ClientIdFieldId = "client-id",
                                SubjectResolution = new OAuthSubjectResolutionDefinition
                                {
                                    Source = OAuthSubjectSource.UserInfo,
                                    Field = "login",
                                },
                                ClientAuthenticationMethod = OAuth2ClientAuthenticationMethod.None,
                            },
                        },
                    ],
                    CapabilitySources =
                    [
                        new NativeCapabilitySourceDefinition { Id = "github-native", Provider = "github" },
                    ],
                },
            ],
            Skills = [new PluginSkillDefinition { ContentPath = "Plugins/github/skills/github/SKILL.md" }],
        };

        public IReadOnlyList<PluginDefinition> List() => [_plugin];

        public PluginDefinition? Find(string pluginId) =>
            string.Equals(pluginId, _plugin.Id, StringComparison.OrdinalIgnoreCase) ? _plugin : null;
    }

    private sealed class McpTestCatalog : IPluginCatalog
    {
        private readonly PluginDefinition _plugin;

        public McpTestCatalog(int sourceCount, bool useMissingConnectionBinding = false)
        {
            var sources = Enumerable
                .Range(1, sourceCount)
                .Select(index =>
                    (CapabilitySourceDefinition)
                        new McpCapabilitySourceDefinition
                        {
                            Id = $"remote-{index}",
                            Transport = new HttpMcpTransportDefinition { Endpoint = "https://mcp.example.test" },
                            CredentialBindings =
                            [
                                new CredentialBindingDefinition
                                {
                                    ValueSource = useMissingConnectionBinding
                                        ? new ConnectionFieldCredentialValueSourceDefinition
                                        {
                                            AuthSchemeId = "oauth2",
                                            FieldId = "mcp-token",
                                        }
                                        : new OAuthAccessTokenCredentialValueSourceDefinition
                                        {
                                            AuthSchemeId = "oauth2",
                                        },
                                    Target = CredentialBindingTarget.HttpHeader,
                                    TargetName = "Authorization",
                                    ValuePrefix = "Bearer ",
                                },
                                new CredentialBindingDefinition
                                {
                                    ValueSource = new OAuthAccessTokenCredentialValueSourceDefinition
                                    {
                                        AuthSchemeId = "other-auth",
                                    },
                                    Target = CredentialBindingTarget.HttpHeader,
                                    TargetName = "X-Other-Auth",
                                },
                            ],
                        }
                )
                .ToArray();
            _plugin = new PluginDefinition
            {
                Id = "mcp-test",
                Version = "1.0.0",
                DisplayName = "MCP test",
                Connectors =
                [
                    new ConnectorDefinition
                    {
                        Id = "remote",
                        DisplayName = "Remote",
                        AuthSchemes =
                        [
                            new AuthSchemeDefinition
                            {
                                Id = "oauth2",
                                DisplayName = "OAuth",
                                Type = AuthSchemeType.OAuth2,
                                OAuth2AuthorizationCode = new OAuth2AuthorizationCodeSettings
                                {
                                    AuthorizationEndpoint = "https://example.test/authorize",
                                    TokenEndpoint = "https://example.test/token",
                                    ClientIdFieldId = "client-id",
                                    SubjectResolution = new OAuthSubjectResolutionDefinition
                                    {
                                        Source = OAuthSubjectSource.UserInfo,
                                        Field = "login",
                                    },
                                    ClientAuthenticationMethod = OAuth2ClientAuthenticationMethod.None,
                                },
                            },
                        ],
                        CapabilitySources = sources,
                    },
                ],
            };
        }

        public IReadOnlyList<PluginDefinition> List() => [_plugin];

        public PluginDefinition? Find(string pluginId) => pluginId == _plugin.Id ? _plugin : null;
    }

    private sealed class UnsafeSkillCatalog : IPluginCatalog
    {
        private readonly PluginDefinition _plugin;

        public UnsafeSkillCatalog(string contentPath = "../outside/SKILL.md")
        {
            _plugin = new PluginDefinition
            {
                Id = "unsafe-plugin",
                Version = "1.0.0",
                DisplayName = "Unsafe",
                Connectors =
                [
                    new ConnectorDefinition
                    {
                        Id = "connector",
                        DisplayName = "Connector",
                        AuthSchemes =
                        [
                            new AuthSchemeDefinition
                            {
                                Id = "api-key",
                                DisplayName = "API key",
                                Type = AuthSchemeType.ApiKey,
                            },
                        ],
                    },
                ],
                Skills = [new PluginSkillDefinition { ContentPath = contentPath }],
            };
        }

        public IReadOnlyList<PluginDefinition> List() => [_plugin];

        public PluginDefinition? Find(string pluginId) => pluginId == _plugin.Id ? _plugin : null;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
