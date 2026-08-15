using Agw.Shared.Contracts.Pagination;
using Agw.Shared.Exceptions;
using Agw.Tools.Contracts.UserMemories;
using Agw.Tools.Controllers;

using Bens.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Tools.Tests;

public sealed class UserMemoriesControllerTests
{
    [Fact]
    public async Task Api_UsesAuthenticatedUserAndNeverReturnsOwnershipId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await UserMemoryAppServiceTests.Fixture.CreateAsync(cancellationToken);
        fixture.SetUserId("user-a");
        var ownerController = new UserMemoriesController(fixture.Service);

        var createResult = await ownerController.CreateAsync(
            new UserMemoryCreateRequest("Preferences", "Answer style", "Use Markdown."),
            cancellationToken);
        var created = Assert.IsType<UserMemoryDetailResponse>(ReadApiResultData(createResult));
        var pageResult = await ownerController.ListPagedAsync(1, 20, cancellationToken);
        var page = Assert.IsType<PagedResult<UserMemorySummaryResponse>>(ReadApiResultData(pageResult));

        Assert.Equal("Preferences", created.Name);
        Assert.Equal("Use Markdown.", created.Content);
        Assert.Single(page.Items);
        Assert.Null(typeof(UserMemoryDetailResponse).GetProperty("UserId"));
        Assert.Null(typeof(UserMemorySummaryResponse).GetProperty("UserId"));
        Assert.Null(typeof(UserMemorySummaryResponse).GetProperty("Content"));

        fixture.SetUserId("user-b");
        var otherUserController = new UserMemoriesController(fixture.Service);
        var hiddenResult = await otherUserController.GetAsync(created.Id, cancellationToken);
        var apiResult = Assert.IsAssignableFrom<IApiResult>(hiddenResult);

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, apiResult.Code);
    }

    private static object ReadApiResultData(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
        var data = result.GetType().GetProperty("Data")?.GetValue(result);
        Assert.NotNull(data);
        return data;
    }
}
