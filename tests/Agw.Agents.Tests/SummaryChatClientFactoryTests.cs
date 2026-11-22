using System.Net;
using System.Net.Http.Headers;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Summaries;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Providers.Contracts;
using Agw.Shared.Data.Entities.Providers;
using Agw.Testing;
using Anthropic.Exceptions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;

namespace Agw.Agents.Tests;

public class SummaryChatClientFactoryTests
{
    [Theory]
    [InlineData(ProviderType.OpenAIChatCompletions)]
    [InlineData(ProviderType.OpenAIResponses)]
    public async Task CreateAsync_OpenAiRuntimeConfiguration_ReturnsOneShotChatClient(ProviderType providerType)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var modelProvider = await SeedModelProviderAsync(
            dbContext,
            providerType,
            "https://example.invalid",
            [CreateAuthConfig("test-key")],
            cancellationToken
        );
        using var httpClientFactory = new RecordingHttpClientFactory();
        var factory = CreateFactory(dbContext, httpClientFactory);

        using var client = await factory.CreateAsync(modelProvider.Id, cancellationToken);

        Assert.NotNull(client);
        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("test-model", metadata.DefaultModelId);
        Assert.Equal(new Uri("https://example.invalid"), metadata.ProviderUri);
        if (providerType == ProviderType.OpenAIResponses)
        {
#pragma warning disable OPENAI001
            Assert.NotNull(client.GetService<OpenAI.Responses.ResponsesClient>());
#pragma warning restore OPENAI001
        }
        else
        {
            Assert.NotNull(client.GetService<OpenAI.Chat.ChatClient>());
        }
    }

    [Fact]
    public async Task CreateAsync_AnthropicRuntimeConfiguration_AddsFiveMinuteTopLevelCacheControl()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await AnthropicPromptCachingTests.LocalAnthropicServer.StartAsync(cancellationToken);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var modelProvider = await SeedModelProviderAsync(
            dbContext,
            ProviderType.Anthropic,
            $"{server.Address}/gateway/anthropic",
            [CreateAuthConfig("test-key")],
            cancellationToken
        );
        await using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var factory = CreateFactory(dbContext, httpClientFactory);

        using var client = await factory.CreateAsync(modelProvider.Id, cancellationToken);
        var exception = await Record.ExceptionAsync(async () =>
            await client!.GetResponseAsync([new(ChatRole.User, "question")], cancellationToken: cancellationToken)
        );

        var apiException = Assert.IsType<AnthropicBadRequestException>(exception);
        Assert.Equal(HttpStatusCode.BadRequest, apiException.StatusCode);
        Assert.True(server.Request.IsCompletedSuccessfully);
        using var request = await server.Request;
        var body = request.RootElement;
        var cacheControl = body.GetProperty("cache_control");
        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());
        Assert.False(cacheControl.TryGetProperty("ttl", out _));
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.Equal(1024, body.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAsync_OllamaConfiguration_ReturnsOwnedChatClient(bool withApiKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var modelProvider = await SeedModelProviderAsync(
            dbContext,
            ProviderType.OllamaNative,
            "http://ollama.test/ollama",
            withApiKey ? [CreateAuthConfig("ollama-key")] : [],
            cancellationToken
        );
        using var httpClientFactory = new RecordingHttpClientFactory();
        var factory = CreateFactory(dbContext, httpClientFactory);

        var client = await factory.CreateAsync(modelProvider.Id, cancellationToken);

        Assert.NotNull(client);
        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("ollama", metadata.ProviderName);
        Assert.Equal("test-model", metadata.DefaultModelId);
        Assert.Equal(new Uri("http://ollama.test/ollama/"), metadata.ProviderUri);
        Assert.NotNull(client.GetService<OllamaApiClient>());
        var httpClient = Assert.Single(httpClientFactory.Clients);
        Assert.Equal(
            withApiKey ? new AuthenticationHeaderValue("Bearer", "ollama-key") : null,
            httpClient.DefaultRequestHeaders.Authorization
        );

        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => httpClient.GetAsync("api/version", cancellationToken));
    }

    [Fact]
    public async Task CreateAsync_OllamaConfiguration_ReportsCachedInputTokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await OllamaChatClientFactoryTests.LocalOllamaServer.StartAsync(cancellationToken);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var dbContext = await CreateDbContextAsync(connection, cancellationToken);
        var modelProvider = await SeedModelProviderAsync(
            dbContext,
            ProviderType.OllamaNative,
            $"{server.Address}/ollama",
            [],
            cancellationToken
        );
        await using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();
        var factory = CreateFactory(dbContext, httpClientFactory);

        using var client = await factory.CreateAsync(modelProvider.Id, cancellationToken);
        var response = await client!.GetResponseAsync([new(ChatRole.User, "7")], cancellationToken: cancellationToken);

        Assert.Equal(7, response.Usage?.CachedInputTokenCount);
    }

    private static async Task<AgwDbContext> CreateDbContextAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;
        var dbContext = new AgwDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
        return dbContext;
    }

    private static async Task<ModelProviderRelation> SeedModelProviderAsync(
        AgwDbContext dbContext,
        ProviderType providerType,
        string endpoint,
        List<ProviderAuthConfig> authConfigs,
        CancellationToken cancellationToken
    )
    {
        var provider = new Provider
        {
            Id = Guid.CreateVersion7(),
            Name = providerType.ToString(),
            ProviderType = providerType,
            Endpoint = endpoint,
            CreateBy = "tester",
            AuthConfigs = authConfigs,
        };
        var model = new AgwAiModel
        {
            Id = Guid.CreateVersion7(),
            Name = "test-model",
            MaxContextWindowTokens = 8192,
            MaxOutputTokens = 2048,
            CreateBy = "tester",
        };
        var modelProvider = new ModelProviderRelation
        {
            Id = Guid.CreateVersion7(),
            ModelId = model.Id,
            ProviderId = provider.Id,
            CreateBy = "tester",
        };
        dbContext.AddRange(provider, model, modelProvider);
        await dbContext.SaveChangesAsync(cancellationToken);
        return modelProvider;
    }

    private static ProviderAuthConfig CreateAuthConfig(string apiKey) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            AuthType = ProviderAuthType.ApiKey,
            ApiKey = apiKey,
            Enable = true,
            CreateBy = "tester",
        };

    private static SummaryChatClientFactory CreateFactory(AgwDbContext dbContext, IHttpClientFactory httpClientFactory)
    {
        var userInfo = new TestUserInfoService();
        var appService = TestAgentAppService.Create(
            null!,
            null!,
            new TestModelProviderReferenceFacade(
                new EfRepository<ModelProviderRelation>(dbContext),
                new EfRepository<AgwAiModel>(dbContext),
                new EfRepository<Provider>(dbContext),
                userInfo
            ),
            null!,
            userInfo,
            null!
        );
        return new SummaryChatClientFactory(
            appService,
            new OllamaChatClientFactory(httpClientFactory),
            NullLogger<SummaryChatClientFactory>.Instance
        );
    }
}
