using System.Collections;
using System.Reflection;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Repositories;
using Agw.Integrations.Contracts.Manager;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Repositories;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Controllers.Tests;

public class IntegrationsControllerTests
{
    [Fact]
    public async Task CreateAppInstanceAsync_WhenRequestIsValid_ReturnsCreatedInstance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await IntegrationControllerTestScope.CreateAsync(cancellationToken);
        var controller = scope.CreateController();
        var request = new AppInstanceCreateRequest("github", "client-id", "client-secret", true);

        var result = await InvokeActionAsync(controller, "CreateAppInstanceAsync", request);

        var created = ReadApiResultData(result);

        Assert.Equal("github", ReadProperty<string>(created, "AppName"));
        Assert.Equal("client-id", ReadProperty<string>(created, "ClientId"));
        Assert.True(ReadProperty<bool>(created, "HasClientSecret"));
        Assert.False(ReadProperty<bool>(created, "IsAuthorized"));
    }

    [Fact]
    public async Task CreateAppInstanceAsync_WhenAppDefinitionDoesNotExist_ReturnsBadRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await IntegrationControllerTestScope.CreateAsync(cancellationToken);
        var controller = scope.CreateController();
        var request = new AppInstanceCreateRequest("missing-app", "client-id", "client-secret", true);

        var result = await InvokeActionAsync(controller, "CreateAppInstanceAsync", request);

        AssertApiResult(result);
    }

    [Fact]
    public async Task ListAppDefinitionsAsync_WhenCalled_ReturnsConfiguredAppDefinitions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await IntegrationControllerTestScope.CreateAsync(cancellationToken);
        var controller = scope.CreateController();

        var result = await InvokeActionAsync(controller, "ListAppDefinitionsAsync");

        var items = Assert.IsAssignableFrom<IEnumerable>(ReadApiResultData(result)).Cast<object>().ToList();

        Assert.Equal(IntegrationConstants.AppList.Count, items.Count);

        var github = items.Single(item => ReadProperty<string>(item, "Name") == "github");
        Assert.Equal("GitHub", ReadProperty<string>(github, "DisplayName"));
        Assert.Equal("GitHub OAuth App", ReadProperty<string>(github, "Provider"));
        Assert.True(ReadProperty<bool>(github, "UsePkce"));
    }

    [Fact]
    public async Task ListAppInstancesAsync_WhenCalled_ReturnsAuthorizationState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var expiredInstanceId = Guid.NewGuid();
        var activeInstanceId = Guid.NewGuid();

        await using var scope = await IntegrationControllerTestScope.CreateAsync(
            cancellationToken,
            dbContext =>
            {
                dbContext.AppInstances.AddRange(
                    new AppInstance
                    {
                        Id = expiredInstanceId,
                        AppName = "github",
                        UsePkce = true,
                        ClientId = "expired-client",
                        ClientSecret = "expired-secret",
                        CreateBy = "tester",
                        CreateTime = now.UtcDateTime.AddMinutes(-10)
                    },
                    new AppInstance
                    {
                        Id = activeInstanceId,
                        AppName = "google-workspace",
                        UsePkce = false,
                        ClientId = "active-client",
                        ClientSecret = "active-secret",
                        CreateBy = "tester",
                        CreateTime = now.UtcDateTime
                    });

                dbContext.OAuthAuthorizationTokens.AddRange(
                    new OAuthAuthorizationToken
                    {
                        Id = Guid.NewGuid(),
                        AppInstanceId = expiredInstanceId,
                        Subject = "expired-user",
                        AccessToken = "expired-token",
                        ExpiresAtUtc = now.AddMinutes(-5)
                    },
                    new OAuthAuthorizationToken
                    {
                        Id = Guid.NewGuid(),
                        AppInstanceId = activeInstanceId,
                        Subject = "active-user",
                        AccessToken = "active-token",
                        ExpiresAtUtc = now.AddMinutes(30)
                    });
            });

        var controller = scope.CreateController();
        var result = await InvokeActionAsync(controller, "ListAppInstancesAsync");

        var items = Assert.IsAssignableFrom<IEnumerable>(ReadApiResultData(result)).Cast<object>().ToList();
        Assert.Equal(2, items.Count);

        var expired = items.Single(item => ReadProperty<Guid>(item, "Id") == expiredInstanceId);
        Assert.Equal("github", ReadProperty<string>(expired, "AppName"));
        Assert.Equal("GitHub", ReadProperty<string>(expired, "DisplayName"));
        Assert.True(ReadProperty<bool>(expired, "HasClientSecret"));
        Assert.False(ReadProperty<bool>(expired, "IsAuthorized"));
        Assert.True(ReadProperty<bool>(expired, "IsAuthorizationExpired"));
        Assert.Equal("expired-user", ReadProperty<string>(expired, "AuthorizationSubject"));

        var active = items.Single(item => ReadProperty<Guid>(item, "Id") == activeInstanceId);
        Assert.Equal("google-workspace", ReadProperty<string>(active, "AppName"));
        Assert.Equal("", ReadProperty<string>(active, "DisplayName"));
        Assert.False(ReadProperty<bool>(active, "UsePkce"));
        Assert.True(ReadProperty<bool>(active, "IsAuthorized"));
        Assert.False(ReadProperty<bool>(active, "IsAuthorizationExpired"));
        Assert.Equal("active-user", ReadProperty<string>(active, "AuthorizationSubject"));
    }

    [Fact]
    public async Task DeleteAppInstanceAsync_WhenInstanceExists_RemovesInstanceAndToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var appInstanceId = Guid.NewGuid();

        await using var scope = await IntegrationControllerTestScope.CreateAsync(
            cancellationToken,
            dbContext =>
            {
                dbContext.AppInstances.Add(new AppInstance
                {
                    Id = appInstanceId,
                    AppName = "github",
                    ClientId = "client-id",
                    ClientSecret = "client-secret",
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });

                dbContext.OAuthAuthorizationTokens.Add(new OAuthAuthorizationToken
                {
                    Id = Guid.NewGuid(),
                    AppInstanceId = appInstanceId,
                    Subject = "subject",
                    AccessToken = "access-token"
                });
            });

        var controller = scope.CreateController();
        var result = await InvokeActionAsync(controller, "DeleteAppInstanceAsync", appInstanceId);

        AssertApiResult(result);

        await using var assertContext = scope.CreateDbContext();
        Assert.Null(await assertContext.AppInstances.FindAsync([appInstanceId], cancellationToken));
        Assert.False(
            await assertContext.OAuthAuthorizationTokens.AnyAsync(
                token => token.AppInstanceId == appInstanceId,
                cancellationToken));
    }

    [Fact]
    public async Task DeleteAppInstanceAsync_WhenInstanceDoesNotExist_ReturnsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await IntegrationControllerTestScope.CreateAsync(cancellationToken);
        var controller = scope.CreateController();

        var result = await InvokeActionAsync(controller, "DeleteAppInstanceAsync", Guid.NewGuid());

        AssertApiResult(result);
    }

    private static async Task<IActionResult> InvokeActionAsync(object controller, string methodName, params object[] arguments)
    {
        var method = controller.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(method);

        var invocationResult = method!.Invoke(controller, arguments);
        var task = Assert.IsAssignableFrom<Task<IActionResult>>(invocationResult);

        return await task;
    }

    private static T ReadProperty<T>(object item, string propertyName)
    {
        var property = item.GetType().GetProperty(propertyName);
        Assert.NotNull(property);

        var value = property!.GetValue(item);
        return Assert.IsType<T>(value);
    }

    private static object ReadApiResultData(IActionResult result)
    {
        AssertApiResult(result);
        var property = result.GetType().GetProperty("Data");
        Assert.NotNull(property);

        var data = property!.GetValue(result);
        Assert.NotNull(data);
        return data;
    }

    private static void AssertApiResult(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
    }

    private sealed class IntegrationControllerTestScope : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AgwDbContext> _options;

        private IntegrationControllerTestScope(SqliteConnection connection, DbContextOptions<AgwDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public static async Task<IntegrationControllerTestScope> CreateAsync(
            CancellationToken cancellationToken,
            Action<AgwDbContext>? seed = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);

            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;

            await using (var setupContext = new AgwDbContext(options))
            {
                await setupContext.Database.EnsureCreatedAsync(cancellationToken);
            }

            if (seed != null)
            {
                await using var seedContext = new AgwDbContext(options);
                seed(seedContext);
                await seedContext.SaveChangesAsync(cancellationToken);
            }

            return new IntegrationControllerTestScope(connection, options);
        }

        public AgwDbContext CreateDbContext() => new(_options);

        public object CreateController()
        {
            var controllerType = typeof(OAuthController).Assembly.GetType("Agw.Integrations.Controllers.IntegrationsController");
            Assert.NotNull(controllerType);

            var appDefinitionRepository = new AppDefinitionRepo();
            var dbContext = CreateDbContext();
            var appInstanceRepository = new EfRepository<AppInstance>(dbContext);
            var unitOfWork = new UnitOfWork(dbContext);

            var constructor = controllerType!.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(IRepository<AppDefinition>),
                    typeof(IRepository<AppInstance>),
                    typeof(IUnitOfWork)
                ],
                modifiers: null);

            if (constructor != null)
            {
                return constructor.Invoke([appDefinitionRepository, appInstanceRepository, unitOfWork]);
            }

            return Activator.CreateInstance(controllerType, nonPublic: true)
                ?? throw new InvalidOperationException("Unable to create IntegrationsController instance.");
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }
}
