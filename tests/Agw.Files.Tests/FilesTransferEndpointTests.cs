using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Files.Abstracts;
using Agw.Files.Api;
using Agw.Files.Application.Files;
using Agw.Files.Infrastructure.Git;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

/// <summary>
/// /api/files 的 stat、download、write、create-directory、move 与 delete 的 recursive 参数：经过真实的 MVC 管道、
/// AgwApiExceptionMiddleware 与 Project 文件系统解析。
/// The stat, download, write, create-directory and move endpoints and the recursive parameter of delete under
/// /api/files, through the real MVC pipeline, AgwApiExceptionMiddleware and Project file system resolution.
/// </summary>
public sealed class FilesTransferEndpointTests : IDisposable
{
    /// <summary>
    /// 写入接口声明的 multipart 上限：内容上限加 1 MiB。The multipart limit declared by the write endpoint.
    /// </summary>
    private const long MultipartLimitBytes = FileAppService.MaxTransferBytes + 1024 * 1024;

    private readonly string _root = Directory
        .CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "files-transfer-endpoint", Guid.CreateVersion7().ToString("N"))
        )
        .FullName;

    private readonly Guid _projectId = Guid.CreateVersion7();
    private readonly Guid _directoryId = Guid.CreateVersion7();

    public FilesTransferEndpointTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Additional);
    }

    private string Workspace => Path.Combine(_root, "workspace");
    private string Additional => Path.Combine(_root, "additional");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Stat_File_ReturnsFileItem()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "a.md"), "hello", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Url("stat", "path=docs/a.md"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).GetProperty("data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("directoryId").ValueKind);
        Assert.Equal("a.md", data.GetProperty("name").GetString());
        Assert.Equal("docs/a.md", data.GetProperty("path").GetString());
        Assert.Equal("file", data.GetProperty("type").GetString());
        Assert.Equal(5, data.GetProperty("size").GetInt64());
        Assert.Equal(TimeSpan.Zero, data.GetProperty("modifiedTime").GetDateTimeOffset().Offset);
    }

    [Fact]
    public async Task Stat_AdditionalDirectory_ReturnsItemWithDirectoryId()
    {
        await File.WriteAllTextAsync(Path.Combine(Additional, "shared.md"), "shared", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Url("stat", $"path=shared.md&directoryId={_directoryId}"), Token);

        var data = (await ReadJsonAsync(response)).GetProperty("data");
        Assert.Equal(_directoryId, data.GetProperty("directoryId").GetGuid());
        Assert.Equal(6, data.GetProperty("size").GetInt64());
    }

    [Theory]
    [InlineData("path=missing.md", HttpStatusCode.NotFound, 404_0007)]
    [InlineData("path=../outside.md", HttpStatusCode.Forbidden, 403_0005)]
    public async Task Stat_MissingOrOutsidePath_ReturnsApiResultError(string query, HttpStatusCode status, int code)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Url("stat", query), Token);

        await AssertErrorAsync(response, status, code);
    }

    [Fact]
    public async Task Download_File_ReturnsOctetStreamWithContentLength()
    {
        byte[] content = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF];
        Directory.CreateDirectory(Path.Combine(Workspace, "assets"));
        await File.WriteAllBytesAsync(Path.Combine(Workspace, "assets", "logo.png"), content, Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Url("download", "path=assets/logo.png"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(content.Length, response.Content.Headers.ContentLength);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync(Token));
    }

    [Theory]
    [InlineData("path=missing.png", HttpStatusCode.NotFound, 404_0007)]
    [InlineData("path=../outside.png", HttpStatusCode.Forbidden, 403_0005)]
    [InlineData("path=", HttpStatusCode.BadRequest, 400_0001)]
    public async Task Download_Failure_ReturnsApiResultError(string query, HttpStatusCode status, int code)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Url("download", query), Token);

        await AssertErrorAsync(response, status, code);
    }

    [Fact]
    public async Task Download_FileLargerThanTransferLimit_ReturnsFileTooLarge()
    {
        CreateSparseFile(Path.Combine(Workspace, "large.bin"), FileAppService.MaxTransferBytes + 1);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Url("download", "path=large.bin"), Token);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.FileTooLarge.Code);
    }

    [Fact]
    public async Task Write_MultipartContentWithCreateParents_WritesFileAndReturnsFileItem()
    {
        byte[] content = [0x00, 0x01, 0xFE, 0xFF];
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PutAsync(
            Url("write", "path=docs/b.bin&overwrite=true&createParents=true"),
            CreateForm(content),
            Token
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).GetProperty("data");
        Assert.Equal("docs/b.bin", data.GetProperty("path").GetString());
        Assert.Equal("file", data.GetProperty("type").GetString());
        Assert.Equal(content.Length, data.GetProperty("size").GetInt64());
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(Workspace, "docs", "b.bin"), Token));
    }

    [Fact]
    public async Task Write_ExistingFile_ReplacesOnlyWithOverwrite()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.md"), "original", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var rejected = await client.PutAsync(Url("write", "path=b.md"), CreateForm("first"u8.ToArray()), Token);
        var kept = await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md"), Token);
        using var replaced = await client.PutAsync(
            Url("write", "path=b.md&overwrite=true"),
            CreateForm("second"u8.ToArray()),
            Token
        );

        await AssertErrorAsync(rejected, HttpStatusCode.BadRequest, ErrorCodes.FileAlreadyExists.Code);
        Assert.Equal("original", kept);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md"), Token));
    }

    [Fact]
    public async Task Write_ExpectedSha256Differs_ReturnsFileChangedConflict()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.md"), "original", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        var staleSha256 = Convert.ToHexStringLower(SHA256.HashData("stale"u8));

        using var response = await client.PutAsync(
            Url("write", $"path=b.md&overwrite=true&expectedSha256={staleSha256}"),
            CreateForm("replaced"u8.ToArray()),
            Token
        );

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.FileChanged.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md"), Token));
    }

    [Fact]
    public async Task Write_MissingParentWithoutCreateParents_ReturnsResourceNotFound()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PutAsync(
            Url("write", "path=docs/b.md&overwrite=true"),
            CreateForm("content"u8.ToArray()),
            Token
        );

        await AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.ResourceNotFound.Code);
        Assert.False(Directory.Exists(Path.Combine(Workspace, "docs")));
    }

    [Fact]
    public async Task Write_PathOutsideRoot_ReturnsFilePathOutsideRoot()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PutAsync(
            Url("write", "path=../escaped.md&overwrite=true&createParents=true"),
            CreateForm("content"u8.ToArray()),
            Token
        );

        await AssertErrorAsync(response, HttpStatusCode.Forbidden, ErrorCodes.FilePathOutsideRoot.Code);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.md")));
    }

    [Theory]
    [InlineData(FileAppService.MaxTransferBytes + 1)]
    [InlineData(MultipartLimitBytes)]
    public async Task Write_ContentOverTransferLimitWithinFormLimit_ReturnsFileTooLargeAndWritesNothing(long length)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await PutSparseContentAsync(client, length);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, ErrorCodes.FileTooLarge.Code);
        Assert.False(File.Exists(Path.Combine(Workspace, "large.bin")));
    }

    [Fact]
    public async Task Write_ContentOverFormLimit_IsRejectedWhileReadingFormAndWritesNothing()
    {
        // 表单上限为内容上限加 1 MiB；超过它的内容在表单读取阶段就被拒绝，不会到达 FileAppService。
        // The form limit is the content limit plus 1 MiB; content beyond it is rejected while the form is read and
        // never reaches FileAppService.
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await PutSparseContentAsync(client, MultipartLimitBytes + 1);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            $"Multipart body length limit {MultipartLimitBytes} exceeded",
            await response.Content.ReadAsStringAsync(Token)
        );
        Assert.False(File.Exists(Path.Combine(Workspace, "large.bin")));
    }

    [Fact]
    public async Task Write_WithoutContentField_ReturnsBadRequest()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var form = new MultipartFormDataContent { { new StringContent("value"), "other" } };

        using var response = await client.PutAsync(Url("write", "path=b.md&overwrite=true"), form, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(Workspace, "b.md")));
    }

    [Fact]
    public async Task Write_JsonBody_ReturnsUnsupportedMediaType()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        using var body = new StringContent("{\"content\":\"text\"}", Encoding.UTF8, "application/json");

        using var response = await client.PutAsync(Url("write", "path=b.md&overwrite=true"), body, Token);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task CreateDirectory_Recursive_CreatesNestedDirectory()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            Url("create-directory", "path=docs/archive&recursive=true"),
            null,
            Token
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).GetProperty("data");
        Assert.Equal("docs/archive", data.GetProperty("path").GetString());
        Assert.Equal("directory", data.GetProperty("type").GetString());
        Assert.True(Directory.Exists(Path.Combine(Workspace, "docs", "archive")));
    }

    [Fact]
    public async Task CreateDirectory_RecursiveOmittedWithMissingParent_ReturnsResourceNotFound()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(Url("create-directory", "path=docs/archive"), null, Token);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, ErrorCodes.ResourceNotFound.Code);
        Assert.False(Directory.Exists(Path.Combine(Workspace, "docs")));
    }

    [Fact]
    public async Task CreateDirectory_FileWithSameName_ReturnsFileAlreadyExists()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "archive"), "file", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            Url("create-directory", "path=archive&recursive=true"),
            null,
            Token
        );

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.FileAlreadyExists.Code);
    }

    [Fact]
    public async Task Move_File_ReturnsDestinationItem()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs", "archive"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "a.md"), "a", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(
            Url("move", "sourcePath=docs/a.md&destinationPath=docs/archive/a.md&overwrite=false"),
            null,
            Token
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).GetProperty("data");
        Assert.Equal("docs/archive/a.md", data.GetProperty("path").GetString());
        Assert.False(File.Exists(Path.Combine(Workspace, "docs", "a.md")));
        Assert.True(File.Exists(Path.Combine(Workspace, "docs", "archive", "a.md")));
    }

    [Fact]
    public async Task Move_ExistingDestinationWithoutOverwrite_ReturnsFileAlreadyExists()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.md"), "source", Token);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.md"), "destination", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(Url("move", "sourcePath=a.md&destinationPath=b.md"), null, Token);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.FileAlreadyExists.Code);
        Assert.Equal("destination", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md"), Token));
    }

    [Theory]
    [InlineData("sourcePath=.&destinationPath=moved")]
    [InlineData("sourcePath=a.md&destinationPath=a.md")]
    public async Task Move_RootOrSamePath_ReturnsInvalidParam(string query)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.md"), "a", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.PostAsync(Url("move", query), null, Token);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, ErrorCodes.InvalidParam.Code);
        Assert.True(File.Exists(Path.Combine(Workspace, "a.md")));
    }

    [Fact]
    public async Task Delete_NonEmptyDirectoryWithRecursiveFalse_ReturnsDirectoryNotEmptyAndKeepsFiles()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs", "archive"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "archive", "a.md"), "a", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.DeleteAsync(Url("delete", "path=docs/archive&recursive=false"), Token);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, ErrorCodes.DirectoryNotEmpty.Code);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(Workspace, "docs", "archive", "a.md"), Token));
    }

    [Fact]
    public async Task Delete_EmptyDirectoryWithRecursiveFalse_RemovesDirectory()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs", "archive"));
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.DeleteAsync(Url("delete", "path=docs/archive&recursive=false"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ReadJsonAsync(response)).GetProperty("data").GetProperty("success").GetBoolean());
        Assert.False(Directory.Exists(Path.Combine(Workspace, "docs", "archive")));
    }

    [Fact]
    public async Task Delete_RecursiveOmitted_RemovesNonEmptyDirectory()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs", "archive"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "archive", "a.md"), "a", Token);
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.DeleteAsync(Url("delete", "path=docs"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(Workspace, "docs")));
    }

    [Fact]
    public async Task Diff_ChangedFile_ReturnsTextsAndUnifiedDiff()
    {
        using var repository = TempGitRepository.InitializeIn(Workspace);
        repository.Write("app.ts", "const a = 1;\n");
        repository.CommitAll();
        repository.Write("app.ts", "const a = 2;\n");
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(Url("diff", "path=app.ts&scope=unstaged"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJsonAsync(response)).GetProperty("data");
        Assert.Contains("+const a = 2;", data.GetProperty("diff").GetString(), StringComparison.Ordinal);
        Assert.False(data.GetProperty("unchanged").GetBoolean());
        Assert.False(data.GetProperty("isBinary").GetBoolean());
        Assert.Equal("const a = 1;\n", data.GetProperty("originalContent").GetString());
        Assert.Equal("const a = 2;\n", data.GetProperty("modifiedContent").GetString());
    }

    private string Url(string action, string query) => $"/api/files/{action}?projectId={_projectId}&{query}";

    /// <summary>
    /// 上传指定长度的内容（稀疏文件，不必占用同样多的磁盘与内存）。
    /// Uploads content of the given length from a sparse file, which needs neither that much disk nor memory.
    /// </summary>
    private async Task<HttpResponseMessage> PutSparseContentAsync(HttpClient client, long length)
    {
        var source = Path.Combine(_root, $"upload-{length}.bin");
        CreateSparseFile(source, length);
        await using var content = File.OpenRead(source);
        return await client.PutAsync(
            Url("write", "path=large.bin&overwrite=true"),
            CreateForm(new StreamContent(content)),
            Token
        );
    }

    private static MultipartFormDataContent CreateForm(byte[] content) => CreateForm(new ByteArrayContent(content));

    private static MultipartFormDataContent CreateForm(HttpContent content)
    {
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { content, "content", "content.bin" } };
    }

    private static void CreateSparseFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(length);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, int code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await ReadJsonAsync(response)).GetProperty("code").GetInt32());
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: Token);
        return document.RootElement.Clone();
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiResult();
        builder.Services.AddSingleton<IProjectFileSystemConfigurationProvider>(
            new WorkspaceConfigurationProvider(
                new ProjectFileSystemConfiguration(
                    "Project",
                    Workspace,
                    "owner",
                    [new ProjectWorkspaceDirectory(_directoryId, Additional)]
                )
            )
        );
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAgwFileSystemResolver>(provider => new ProjectScopedFileSystemResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TestFileSystems.PathLocks,
            NullLogger<ProjectScopedFileSystemResolver>.Instance,
            NullLogger<LocalFileSystem>.Instance,
            TimeProvider.System
        ));
        builder.Services.AddSingleton<IGitCommandService>(
            new GitCommandService(NullLogger<GitCommandService>.Instance)
        );
        builder.Services.AddSingleton<FileAppService>();
        builder.Services.AddControllers().AddApplicationPart(typeof(FilesController).Assembly);
        var app = builder.Build();
        app.UseMiddleware<AgwApiExceptionMiddleware>();
        app.MapControllers();
        await app.StartAsync(Token);
        return app;
    }

    private sealed class WorkspaceConfigurationProvider : IProjectFileSystemConfigurationProvider
    {
        private readonly ProjectFileSystemConfiguration _configuration;

        public WorkspaceConfigurationProvider(ProjectFileSystemConfiguration configuration)
        {
            _configuration = configuration;
        }

        public Task<ProjectFileSystemConfiguration?> GetAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ProjectFileSystemConfiguration?>(_configuration);
    }
}
