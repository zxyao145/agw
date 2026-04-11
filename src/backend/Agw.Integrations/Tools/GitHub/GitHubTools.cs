using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using Agw.Integrations.Domain.Entities;
using Agw.Integrations.Tools.GitHub.Dtos;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;


namespace Agw.Integrations.Tools.GitHub;


/// <summary>
/// Provides basic utility tools for agents.
/// </summary>
[AiToolContainer(DefaultCategory = "Git")]
public class GitHubTools
{
    private const string APP_NAME = "github";
    private const string LIST_REPO_API = "https://api.github.com/user/repos";


    [AiTool("github_list_repository")]
    [Description("list user's repositories")]
    public static async Task<List<GitHubRepoInfo>> ListRepositories
        (
        CancellationToken cancellationToken = default
        )
    {
        var appInstance = await GetAppInfoAsync();
        if (appInstance == null || appInstance.AuthorizationToken == null)
        {
            throw new Exception("not found app oauth token");
        }

        using var scope = IocUtil.ServiceProvider.CreateScope();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        HttpClient client = httpClientFactory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, LIST_REPO_API);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appInstance.AuthorizationToken.AccessToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("agw/1.0");

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

            var logger = IocUtil.CreateLogger<GitHubTools>();
            logger.LogError("Failed to list repositories. StatusCode: {StatusCode}, ReasonPhrase: {ReasonPhrase}, ResponseContent: {ResponseContent}",
                    (int)response.StatusCode,
                    response.ReasonPhrase,
                    responseContent);

            throw new HttpRequestException($"Bad response status code:{response.StatusCode}");
        }

        var gitHubRepoInfos = await response.Content
                .ReadFromJsonAsync<List<GitHubRepoInfo>>(cancellationToken: cancellationToken)
                ?? new List<GitHubRepoInfo>();
        return gitHubRepoInfos;
    }

    [AiTool("github_clone")]
    [Description("clone a git repository")]
    public static async Task<CloneResult> Clone
        (
       [NotNull, Description("github user namr")] string userName,
       [NotNull, Description("remote git repository address")] string gitAddress,
       [NotNull, Description("local workspace path")] string workspace,
        CancellationToken cancellationToken = default
        )
    {
        using var scope = IocUtil.ServiceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<AppInstance>>();

        var appInstance = await GetAppInfoAsync();
        if (appInstance == null || appInstance.AuthorizationToken == null)
        {
            return new CloneResult(
                success: false,
                error: $"not found app oauth token",
                stdout: null,
                stderr: null
            );
        }

        var cloneUrl = BuildGitCloneUrl(gitAddress, userName, appInstance.AuthorizationToken.AccessToken);
        var gitCommand = scope.ServiceProvider.GetRequiredService<IGitCommandService>();
        var result = await gitCommand.CloneRepositoryAsync(cloneUrl, workspace, cancellationToken);

        return new CloneResult(
            success: result.Success,
            error: result.Error,
            stdout: result.Stdout,
            stderr: result.Stderr
        );
    }

    private static async Task<AppInstance?> GetAppInfoAsync()
    {
        using var scope = IocUtil.ServiceProvider.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<AppInstance>>();
        var appInstance = await repo.Queryable
            .Where(x => x.AppName == APP_NAME)
            .Include(c => c.AuthorizationToken)
            .AsNoTracking()
            .FirstOrDefaultAsync();
        return appInstance;
    }


    private static string BuildGitCloneUrl(string gitAddress, string username, string token)
    {
        if (string.IsNullOrWhiteSpace(gitAddress))
            throw new ArgumentException("gitAddress 不能为空");

        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("username 不能为空");

        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token 不能为空");

        var uri = new Uri(gitAddress);

        // 只允许 https
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("只支持 https 地址");

        var builder = new UriBuilder(uri)
        {
            UserName = username,
            Password = token
        };

        return builder.Uri.ToString();
    }
}
