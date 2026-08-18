using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Application.Management;
using Agw.Integrations.Tools.GitHub.Dtos;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Tools.GitHub;

public sealed partial class GitHubConnectionInvoker : IGitHubConnectionInvoker
{
    private static readonly Uri CurrentUserEndpoint = new("https://api.github.com/user");
    private static readonly Uri RepositoriesEndpoint = new("https://api.github.com/user/repos?per_page=100");
    private const string AuthorizationEnvironmentVariable = "AGW_GITHUB_AUTHORIZATION";

    private readonly IConnectionCredentialReader _credentialReader;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProjectWorkspaceResolver _workspaceResolver;
    private readonly IGitHubGitProcessRunner _gitProcessRunner;

    public GitHubConnectionInvoker(
        IConnectionCredentialReader credentialReader,
        IHttpClientFactory httpClientFactory,
        IProjectWorkspaceResolver workspaceResolver
    )
        : this(credentialReader, httpClientFactory, workspaceResolver, new GitHubGitProcessRunner()) { }

    internal GitHubConnectionInvoker(
        IConnectionCredentialReader credentialReader,
        IHttpClientFactory httpClientFactory,
        IProjectWorkspaceResolver workspaceResolver,
        IGitHubGitProcessRunner gitProcessRunner
    )
    {
        _credentialReader = credentialReader;
        _httpClientFactory = httpClientFactory;
        _workspaceResolver = workspaceResolver;
        _gitProcessRunner = gitProcessRunner;
    }

    public async Task<GitHubUserInfo> GetCurrentUserAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var token = await ReadAccessTokenAsync(connectionId, cancellationToken);
        return await SendAsync<GitHubUserInfo>(CurrentUserEndpoint, token, cancellationToken) ?? throw RemoteFailure();
    }

    public async Task<IReadOnlyList<GitHubRepoInfo>> ListRepositoriesAsync(
        Guid connectionId,
        CancellationToken cancellationToken
    )
    {
        var token = await ReadAccessTokenAsync(connectionId, cancellationToken);
        return await SendAsync<List<GitHubRepoInfo>>(RepositoriesEndpoint, token, cancellationToken) ?? [];
    }

    public async Task<CloneResult> CloneRepositoryAsync(
        Guid connectionId,
        Guid projectId,
        string owner,
        string repository,
        string? relativePath,
        CancellationToken cancellationToken
    )
    {
        if (!RepositoryPartRegex().IsMatch(owner) || !RepositoryPartRegex().IsMatch(repository))
        {
            throw new AgwException(ErrorCodes.GitHubRepositoryInvalid);
        }

        var workspace = await _workspaceResolver.ResolveWorkspaceAsync(projectId, cancellationToken);
        if (string.IsNullOrWhiteSpace(workspace))
        {
            throw new AgwException(ErrorCodes.GitHubProjectWorkspaceNotFound);
        }

        var workspacePath = ExpandTilde(workspace);
        var targetPath = ResolveCloneTarget(workspacePath, relativePath ?? repository);
        var token = await ReadAccessTokenAsync(connectionId, cancellationToken);
        var authorizationHeader = CreateGitAuthorizationHeader(token);
        var remoteUrl = $"https://github.com/{owner}/{repository}.git";
        var request = new GitHubGitProcessRequest
        {
            WorkingDirectory = workspacePath,
            Arguments =
            [
                "-c",
                "credential.helper=",
                "-c",
                "http.followRedirects=false",
                $"--config-env=http.https://github.com/.extraHeader={AuthorizationEnvironmentVariable}",
                "clone",
                "--",
                remoteUrl,
                targetPath,
            ],
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AuthorizationEnvironmentVariable] = authorizationHeader,
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GCM_INTERACTIVE"] = "Never",
                ["GIT_ASKPASS"] = string.Empty,
                ["SSH_ASKPASS"] = string.Empty,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
                ["GIT_ATTR_NOSYSTEM"] = "1",
            },
            SecretsToRedact = [token, authorizationHeader],
        };

        GitHubGitProcessResult result;
        try
        {
            result = await _gitProcessRunner.RunCloneAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgwException)
        {
            throw;
        }
        catch
        {
            throw new AgwException(ErrorCodes.GitHubCloneFailed);
        }

        return new CloneResult(
            result.ExitCode == 0,
            result.ExitCode == 0 ? null : "GitHub clone failed.",
            Redact(result.StandardOutput, request.SecretsToRedact),
            Redact(result.StandardError, request.SecretsToRedact)
        );
    }

    internal static string ResolveCloneTarget(string workspace, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw InvalidClonePath();
        }

        var workspacePath = Path.GetFullPath(workspace);
        var targetPath = Path.GetFullPath(Path.Combine(workspacePath, relativePath));
        var resolvedRelativePath = Path.GetRelativePath(workspacePath, targetPath);
        if (
            Path.IsPathRooted(resolvedRelativePath)
            || resolvedRelativePath == ".."
            || resolvedRelativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw InvalidClonePath();
        }

        var workspaceInfo = new DirectoryInfo(workspacePath);
        var workspaceTarget =
            workspaceInfo.LinkTarget == null ? null : workspaceInfo.ResolveLinkTarget(returnFinalTarget: true);
        var physicalWorkspace = Path.GetFullPath(workspaceTarget?.FullName ?? workspacePath);
        var physicalTarget = physicalWorkspace;
        foreach (var segment in resolvedRelativePath.Split(Path.DirectorySeparatorChar))
        {
            var candidate = Path.Combine(physicalTarget, segment);
            var candidateInfo = new DirectoryInfo(candidate);
            var candidateTarget =
                candidateInfo.LinkTarget == null ? null : candidateInfo.ResolveLinkTarget(returnFinalTarget: true);
            physicalTarget = Path.GetFullPath(candidateTarget?.FullName ?? candidate);
            if (!IsWithinDirectory(physicalWorkspace, physicalTarget))
            {
                throw InvalidClonePath();
            }
        }

        return physicalTarget;
    }

    private static bool IsWithinDirectory(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(root, path, comparison)
            || path.StartsWith(
                string.Concat(root.TrimEnd(Path.DirectorySeparatorChar), Path.DirectorySeparatorChar),
                comparison
            );
    }

    private async Task<string> ReadAccessTokenAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        try
        {
            var credential = await _credentialReader.ReadConnectionAsync(
                connectionId,
                IntegrationCredentialSlots.OAuthAccessToken,
                cancellationToken
            );
            if (credential != null && !string.IsNullOrWhiteSpace(credential.Value))
            {
                return credential.Value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch { }

        throw new AgwException(ErrorCodes.IntegrationCredentialUnavailable);
    }

    private async Task<T?> SendAsync<T>(Uri endpoint, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, endpoint, token);
            using var response = await _httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw RemoteFailure();
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgwException)
        {
            throw;
        }
        catch
        {
            throw RemoteFailure();
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint, string token)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("agw/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static string CreateGitAuthorizationHeader(string token)
    {
        var basicValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"));
        return $"Authorization: Basic {basicValue}";
    }

    private static string? Redact(string? value, IEnumerable<string> secrets)
    {
        if (value == null)
        {
            return null;
        }

        foreach (var secret in secrets.Where(item => !string.IsNullOrEmpty(item)))
        {
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }

    private static string ExpandTilde(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (path.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        return path;
    }

    private static AgwException InvalidClonePath()
    {
        return new AgwException(ErrorCodes.GitHubClonePathInvalid);
    }

    private static AgwException RemoteFailure()
    {
        return new AgwException(ErrorCodes.GitHubBadResponseStatusCode);
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,99})$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPartRegex();
}

internal interface IGitHubGitProcessRunner
{
    Task<GitHubGitProcessResult> RunCloneAsync(GitHubGitProcessRequest request, CancellationToken cancellationToken);
}

internal sealed class GitHubGitProcessRequest
{
    public required string WorkingDirectory { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public required IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }

    public required IReadOnlyList<string> SecretsToRedact { get; init; }
}

internal sealed record GitHubGitProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class GitHubGitProcessRunner : IGitHubGitProcessRunner
{
    public async Task<GitHubGitProcessResult> RunCloneAsync(
        GitHubGitProcessRequest request,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(request.WorkingDirectory);
        using var process = new Process { StartInfo = CreateStartInfo(request) };

        try
        {
            process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new GitHubGitProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw new AgwException(ErrorCodes.GitHubCloneFailed);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(GitHubGitProcessRequest request)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        HardenGitEnvironment(startInfo.Environment);

        foreach (var pair in request.EnvironmentVariables)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    internal static void HardenGitEnvironment(IDictionary<string, string?> environment)
    {
        foreach (
            var inheritedName in environment
                .Keys.Where(item => item.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToArray()
        )
        {
            environment.Remove(inheritedName);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch { }
    }
}
