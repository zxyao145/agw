using System.IO.Compression;
using System.Net;
using System.Text;

using Agw.Agents.Execution.Agents.Skills;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests;

public class RemoteSkillHttpClientTests
{
    [Fact]
    public async Task FetchAsync_ValidResponse_ReturnsNormalizedDefinition()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = CreateArchiveContent(
                """
                ---
                name: expense-report
                description: Enterprise expense policy
                ---
                Complete skill instructions...
                """),
        });
        var client = CreateClient(handler);

        var result = await client.FetchAsync(
            "https://example.com/skills/expense-report.zip",
            TestContext.Current.CancellationToken);

        Assert.Equal("expense-report", result.Name);
        Assert.Equal("Enterprise expense policy", result.Description);
        Assert.Equal("Complete skill instructions...", result.Instructions);
        Assert.Empty(result.Tags);
        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
        Assert.Equal(
            "https://example.com/skills/expense-report.zip",
            handler.LastRequest?.RequestUri?.AbsoluteUri);
        Assert.Null(handler.LastRequest?.Headers.Authorization);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("ftp://example.com/skill")]
    public async Task FetchAsync_InvalidUrl_ThrowsRemoteSkillUrlInvalid(string remoteUrl)
    {
        var client = CreateClient(new StubHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(remoteUrl, TestContext.Current.CancellationToken));

        Assert.Equal(
            string.IsNullOrWhiteSpace(remoteUrl)
                ? ErrorCodes.RemoteSkillUrlRequired.Code
                : ErrorCodes.RemoteSkillUrlInvalid.Code,
            exception.Code);
    }

    [Theory]
    [InlineData("# Missing frontmatter")]
    [InlineData("---\nname: expense-report\n---\nbody")]
    [InlineData("---\nname: Expense Report\ndescription: desc\n---\nbody")]
    [InlineData("---\nname: expense-report\ndescription: desc\n---")]
    public async Task FetchAsync_InvalidSkillMarkdown_ThrowsRemoteSkillResponseInvalid(
        string skillMarkdown)
    {
        var client = CreateClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = CreateArchiveContent(skillMarkdown),
            }));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(
                "https://example.com/skill",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillResponseInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task FetchAsync_ResponseIsNotZip_ThrowsRemoteSkillResponseInvalid()
    {
        var client = CreateClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not a zip archive"),
            }));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(
                "https://example.com/skill.zip",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillResponseInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task FetchAsync_ArchiveWithoutSkillMarkdown_ThrowsRemoteSkillResponseInvalid()
    {
        var client = CreateClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = CreateArchiveContent("# Read me", "expense-report/README.md"),
            }));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(
                "https://example.com/skill.zip",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillResponseInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task FetchAsync_ArchiveWithMultipleSkills_ThrowsRemoteSkillResponseInvalid()
    {
        var content = CreateArchiveContent(
            """
            ---
            name: expense-report
            description: Expense policy
            ---
            First instructions.
            """,
            "expense-report/SKILL.md",
            ("another/SKILL.md",
                """
                ---
                name: another
                description: Another skill
                ---
                Other instructions.
                """));
        var client = CreateClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(
                "https://example.com/skill.zip",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillResponseInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task FetchAsync_NonSuccessResponse_ThrowsRemoteSkillFetchFailed()
    {
        var client = CreateClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(
                "https://example.com/skill",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillFetchFailed.Code, exception.Code);
    }

    [Fact]
    public async Task FetchAsync_ResponseExceedsLimit_ThrowsRemoteSkillResponseInvalid()
    {
        var content = new ByteArrayContent([]);
        content.Headers.ContentLength = RemoteSkillHttpClient.MaxResponseBytes + 1;
        var client = CreateClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            }));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(
                "https://example.com/skill",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillResponseInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task FetchAsync_HttpClientTimeout_ThrowsRemoteSkillFetchFailed()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = CreateClient(handler, TimeSpan.FromMilliseconds(20));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            client.FetchAsync(
                "https://example.com/skill",
                TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.RemoteSkillFetchFailed.Code, exception.Code);
    }

    private static ByteArrayContent CreateArchiveContent(
        string content,
        string path = "expense-report/SKILL.md",
        params (string Path, string Content)[] additionalEntries)
    {
        using var archiveStream = new MemoryStream();
        using (var archive = new ZipArchive(
            archiveStream,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            WriteEntry(archive, path, content);
            foreach (var entry in additionalEntries)
            {
                WriteEntry(archive, entry.Path, entry.Content);
            }
        }

        var result = new ByteArrayContent(archiveStream.ToArray());
        result.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        return result;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static RemoteSkillHttpClient CreateClient(
        HttpMessageHandler handler,
        TimeSpan? timeout = null)
    {
        var httpClient = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        return new RemoteSkillHttpClient(new StubHttpClientFactory(httpClient));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(RemoteSkillHttpClient.HttpClientName, name);
            return _client;
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this((request, _) => Task.FromResult(handler(request)))
        {
        }

        public StubHttpMessageHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _handler(request, cancellationToken);
        }
    }
}
