using System.Security.Claims;
using Agw.Auth.Application;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Infrastructure.Repositories;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Exceptions;
using Agw.Tools.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Tools.Tests;

public sealed class UserMemoryAppServiceTests
{
    [Fact]
    public async Task CreateAsync_SameNameAcrossUsersIsolatedAndContentEncrypted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(cancellationToken);

        var first = await fixture.Service.CreateAsync(
            " Preferences ",
            "How I like answers",
            "Use concise Markdown.",
            cancellationToken
        );
        fixture.SetUserId("user-b");
        var second = await fixture.Service.CreateAsync("preferences", null, "Use detailed answers.", cancellationToken);

        Assert.Equal("Preferences", first.Name);
        Assert.Null(await fixture.Service.GetAsync(first.Id, cancellationToken));
        fixture.SetUserId("user-a");
        Assert.Null(await fixture.Service.GetAsync(second.Id, cancellationToken));
        Assert.Equal(
            "Use concise Markdown.",
            (await fixture.Service.GetByNameAsync("PREFERENCES", cancellationToken))?.Content
        );
        Assert.Equal(1, (await fixture.Service.ListPageAsync(1, 20, cancellationToken)).Total);
        fixture.SetUserId("user-b");
        Assert.Equal(1, (await fixture.Service.ListPageAsync(1, 20, cancellationToken)).Total);
        fixture.SetUserId("user-a");

        var duplicate = await Assert.ThrowsAsync<AgwException>(() =>
            fixture.Service.CreateAsync("preferences", null, "duplicate", cancellationToken)
        );
        Assert.Equal(ErrorCodes.UserMemoryNameAlreadyExists.Code, duplicate.Code);

        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT content, description FROM user_memory WHERE user_id = 'user-a'";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        var storedContent = reader.GetString(0);
        Assert.NotEqual("Use concise Markdown.", storedContent);
        Assert.DoesNotContain("concise Markdown", storedContent, StringComparison.Ordinal);
        Assert.Equal("How I like answers", reader.GetString(1));
    }

    [Fact]
    public async Task UpsertByNameAsync_OmittedDescriptionPreservesAndEmptyDescriptionClears()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(cancellationToken);
        await fixture.Service.CreateAsync("Profile", "Personal preferences", "Original", cancellationToken);

        var preserved = await fixture.Service.UpsertByNameAsync(
            "profile",
            "Updated",
            description: null,
            cancellationToken
        );
        Assert.Equal("Personal preferences", preserved.Description);
        Assert.Equal("Updated", preserved.Content);

        var cleared = await fixture.Service.UpsertByNameAsync(
            "PROFILE",
            "Updated again",
            description: "",
            cancellationToken
        );
        Assert.Null(cleared.Description);
        Assert.Equal("Updated again", cleared.Content);
    }

    [Fact]
    public async Task Mutations_InvalidFieldsReturnStableErrorCodes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(cancellationToken);

        await AssertCodeAsync(
            ErrorCodes.UserMemoryNameRequired,
            () => fixture.Service.CreateAsync(" ", null, "content", cancellationToken)
        );
        await AssertCodeAsync(
            ErrorCodes.UserMemoryNameTooLong,
            () => fixture.Service.CreateAsync(new string('n', 65), null, "content", cancellationToken)
        );
        await AssertCodeAsync(
            ErrorCodes.UserMemoryDescriptionTooLong,
            () => fixture.Service.CreateAsync("name", new string('d', 301), "content", cancellationToken)
        );
        await AssertCodeAsync(
            ErrorCodes.UserMemoryContentRequired,
            () => fixture.Service.CreateAsync("name", null, "\n ", cancellationToken)
        );
    }

    private static async Task AssertCodeAsync(ErrorCode errorCode, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<AgwException>(action);
        Assert.Equal(errorCode.Code, exception.Code);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            SqliteConnection connection,
            AgwDbContext context,
            UserMemoryAppService service,
            TestUserInfoService userInfoService
        )
        {
            Connection = connection;
            Context = context;
            Service = service;
            UserInfoService = userInfoService;
        }

        public SqliteConnection Connection { get; }

        public AgwDbContext Context { get; }

        public UserMemoryAppService Service { get; }

        internal TestUserInfoService UserInfoService { get; }

        public void SetUserId(string userId) => UserInfoService.SetUserId(userId);

        public static async Task<Fixture> CreateAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var protector = new DataProtectionEncryptedDataProtector(new EphemeralDataProtectionProvider());
            var context = new AgwDbContext(options, protector);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            var repository = new EfRepository<UserMemory>(context);
            var userInfoService = new TestUserInfoService("user-a");
            var service = new UserMemoryAppService(repository, context, new InMemoryApplicationLock(), userInfoService);
            return new Fixture(connection, context, service, userInfoService);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    internal sealed class TestUserInfoService : IUserInfoService
    {
        public TestUserInfoService(string userId)
        {
            SetUserId(userId);
        }

        public ClaimsPrincipal? Current { get; set; }

        public string? UserId => Current?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Current?.Identity?.Name;

        public bool IsAuthenticated => Current?.Identity?.IsAuthenticated == true;

        public string RequiredUserId => UserId ?? throw new InvalidOperationException("A test user is required.");

        public void SetUserId(string userId)
        {
            Current = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));
        }
    }
}
