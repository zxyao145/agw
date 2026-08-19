using System.Net;
using System.Text;
using System.Text.Json;
using Agw.Integrations.Application.Credentials;
using Agw.Integrations.Tools.GitHub;
using Agw.Shared.Exceptions;

namespace Agw.Integrations.Tests;

public class GitHubConnectionInvokerTests
{
    [Fact]
    public async Task GetCurrentUser_EachInvocationReadsLatestCredential()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var connectionId = Guid.CreateVersion7();
        var credentials = new MutableCredentialReader();
        var handler = new RecordingHttpHandler();
        var invoker = new GitHubConnectionInvoker(
            credentials,
            new FixedHttpClientFactory(handler),
            new FixedWorkspaceResolver(Path.GetTempPath()),
            new RecordingGitRunner()
        );

        credentials.Token = "token-v1";
        await invoker.GetCurrentUserAsync(connectionId, cancellationToken);
        credentials.Token = "token-v2";
        await invoker.GetCurrentUserAsync(connectionId, cancellationToken);

        Assert.Equal(["Bearer token-v1", "Bearer token-v2"], handler.AuthorizationValues);
    }

    [Fact]
    public async Task CloneRepository_BuildsWorkspaceBoundSecretSafeGitCommandAndRedactsOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Directory.CreateTempSubdirectory("agw-github-workspace-").FullName;
        try
        {
            var credentials = new MutableCredentialReader { Token = "clone-secret-value" };
            var runner = new RecordingGitRunner
            {
                Result = new GitHubGitProcessResult(
                    1,
                    "stdout clone-secret-value",
                    "stderr Authorization: Basic eC1hY2Nlc3MtdG9rZW46Y2xvbmUtc2VjcmV0LXZhbHVl"
                ),
            };
            var invoker = new GitHubConnectionInvoker(
                credentials,
                new FixedHttpClientFactory(new RecordingHttpHandler()),
                new FixedWorkspaceResolver(workspace),
                runner
            );

            var result = await invoker.CloneRepositoryAsync(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "openai",
                "codex",
                "sources/codex",
                cancellationToken
            );

            var request = Assert.Single(runner.Requests);
            Assert.Equal(Path.GetFullPath(workspace), request.WorkingDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(workspace), "sources", "codex"), request.Arguments[^1]);
            Assert.Contains("https://github.com/openai/codex.git", request.Arguments);
            Assert.Contains(
                request.Arguments,
                argument =>
                    argument.StartsWith("--config-env=http.https://github.com/.extraHeader=", StringComparison.Ordinal)
            );
            Assert.DoesNotContain(
                request.Arguments,
                argument => argument.Contains("clone-secret-value", StringComparison.Ordinal)
            );
            Assert.DoesNotContain(
                request.Arguments,
                argument => argument.Contains("Authorization:", StringComparison.Ordinal)
            );
            Assert.Equal("0", request.EnvironmentVariables["GIT_TERMINAL_PROMPT"]);
            Assert.Equal("Never", request.EnvironmentVariables["GCM_INTERACTIVE"]);
            Assert.DoesNotContain("clone-secret-value", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("clone-secret-value", result.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Authorization: Basic", result.Stderr, StringComparison.Ordinal);
            Assert.Equal("GitHub clone failed.", result.Error);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData("/tmp/escape")]
    [InlineData("../escape")]
    [InlineData("child/../../escape")]
    public async Task CloneRepository_AbsoluteOrTraversalTarget_IsRejectedBeforeCredentialOrProcess(string relativePath)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var credentials = new MutableCredentialReader { Token = "must-not-be-read" };
        var runner = new RecordingGitRunner();
        var invoker = new GitHubConnectionInvoker(
            credentials,
            new FixedHttpClientFactory(new RecordingHttpHandler()),
            new FixedWorkspaceResolver(Path.GetTempPath()),
            runner
        );

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            invoker.CloneRepositoryAsync(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                "openai",
                "codex",
                relativePath,
                cancellationToken
            )
        );

        Assert.Equal(ErrorCodes.GitHubClonePathInvalid.Code, error.Code);
        Assert.Equal(0, credentials.ReadCount);
        Assert.Empty(runner.Requests);
        Assert.DoesNotContain("must-not-be-read", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloneRepository_TargetThroughWorkspaceSymlink_IsRejectedBeforeCredentialOrProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var workspace = Directory.CreateTempSubdirectory("agw-clone-root-").FullName;
        var outside = Directory.CreateTempSubdirectory("agw-clone-outside-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(workspace, "linked"), outside);
            var credentials = new MutableCredentialReader { Token = "must-not-be-read" };
            var runner = new RecordingGitRunner();
            var invoker = new GitHubConnectionInvoker(
                credentials,
                new FixedHttpClientFactory(new RecordingHttpHandler()),
                new FixedWorkspaceResolver(workspace),
                runner
            );

            var error = await Assert.ThrowsAsync<AgwException>(() =>
                invoker.CloneRepositoryAsync(
                    Guid.CreateVersion7(),
                    Guid.CreateVersion7(),
                    "openai",
                    "codex",
                    "linked/codex",
                    cancellationToken
                )
            );

            Assert.Equal(ErrorCodes.GitHubClonePathInvalid.Code, error.Code);
            Assert.Equal(0, credentials.ReadCount);
            Assert.Empty(runner.Requests);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteFailure_DoesNotExposeResponseBodyOrToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var credentials = new MutableCredentialReader { Token = "api-secret" };
        var handler = new RecordingHttpHandler
        {
            StatusCode = HttpStatusCode.InternalServerError,
            Body = "server echoed api-secret",
        };
        var invoker = new GitHubConnectionInvoker(
            credentials,
            new FixedHttpClientFactory(handler),
            new FixedWorkspaceResolver(Path.GetTempPath()),
            new RecordingGitRunner()
        );

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            invoker.GetCurrentUserAsync(Guid.CreateVersion7(), cancellationToken)
        );

        Assert.Equal(ErrorCodes.GitHubBadResponseStatusCode.Code, error.Code);
        Assert.DoesNotContain("api-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("server echoed", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HardenGitEnvironment_RemovesInheritedGitConfigurationAndTraceVariables()
    {
        IDictionary<string, string?> environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "http.followRedirects",
            ["GIT_CONFIG_VALUE_0"] = "true",
            ["GIT_CONFIG_PARAMETERS"] = "'credential.helper'='malicious'",
            ["GIT_TRACE_CURL"] = "1",
            ["GIT_SSH_COMMAND"] = "malicious-command",
            ["PATH"] = "/usr/bin",
        };

        GitHubGitProcessRunner.HardenGitEnvironment(environment);

        Assert.DoesNotContain(environment.Keys, key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/usr/bin", environment["PATH"]);
    }

    [Fact]
    public async Task HttpTransportException_IsSanitizedAndPreservesCancellation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var credentials = new MutableCredentialReader { Token = "transport-secret" };
        var handler = new RecordingHttpHandler
        {
            Exception = new HttpRequestException("transport echoed transport-secret"),
        };
        var invoker = new GitHubConnectionInvoker(
            credentials,
            new FixedHttpClientFactory(handler),
            new FixedWorkspaceResolver(Path.GetTempPath()),
            new RecordingGitRunner()
        );

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            invoker.GetCurrentUserAsync(Guid.CreateVersion7(), cancellationToken)
        );

        Assert.Equal(ErrorCodes.GitHubBadResponseStatusCode.Code, error.Code);
        Assert.DoesNotContain("transport-secret", error.ToString(), StringComparison.Ordinal);

        handler.Exception = new OperationCanceledException(cancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            invoker.GetCurrentUserAsync(Guid.CreateVersion7(), cancellationToken)
        );
    }

    private sealed class MutableCredentialReader : IConnectionCredentialReader
    {
        public string? Token { get; set; }
        public int ReadCount { get; private set; }

        public Task<ResolvedCredential?> ReadConnectionAsync(
            Guid connectionId,
            string slot,
            CancellationToken cancellationToken
        )
        {
            ReadCount++;
            return Task.FromResult(Token == null ? null : new ResolvedCredential { Value = Token });
        }

        public Task<ResolvedCredential?> ReadPluginInstallationAsync(
            Guid pluginInstallationId,
            string slot,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult<ResolvedCredential?>(null);
        }
    }

    private sealed class FixedWorkspaceResolver : IProjectWorkspaceResolver
    {
        private readonly string? _workspace;

        public FixedWorkspaceResolver(string? workspace)
        {
            _workspace = workspace;
        }

        public Task<string?> ResolveWorkspaceAsync(Guid projectId, CancellationToken cancellationToken)
        {
            return Task.FromResult(_workspace);
        }
    }

    private sealed class RecordingGitRunner : IGitHubGitProcessRunner
    {
        public List<GitHubGitProcessRequest> Requests { get; } = [];

        public GitHubGitProcessResult Result { get; set; } = new(0, string.Empty, string.Empty);

        public Task<GitHubGitProcessResult> RunCloneAsync(
            GitHubGitProcessRequest request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FixedHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public List<string> AuthorizationValues { get; } = [];

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = JsonSerializer.Serialize(new GitHubUserInfo { Login = "octocat" });

        public Exception? Exception { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            AuthorizationValues.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            if (Exception != null)
            {
                return Task.FromException<HttpResponseMessage>(Exception);
            }

            return Task.FromResult(
                new HttpResponseMessage(StatusCode)
                {
                    Content = new StringContent(Body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
