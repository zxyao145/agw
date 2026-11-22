using System.Text;
using Agw.Tools.Impl.ToolBlocks.Storage;

namespace Agw.Tools.Tests;

public sealed class ProjectAgentFileReadPagerTests : IDisposable
{
    private readonly string _root = Directory
        .CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "project-agent-file-read-pager", Guid.CreateVersion7().ToString("N"))
        )
        .FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ReadPageAsync_LiteralUtf8Replacement_UsesExactByteOffsetsForEveryPage()
    {
        const int pageSize = ProjectAgentFileReadPager.MaximumPageContentCharacters;
        const int totalCharacters = 1_048_576;
        var token = TestContext.Current.CancellationToken;
        var content = "\uFFFD" + new string('a', totalCharacters - 1);
        var path = await WriteFileAsync("literal-replacement.txt", Encoding.UTF8.GetBytes(content), token);

        FileReadContinuation? continuation = null;
        var pages = new List<string>();
        do
        {
            var page = await ReadPageAsync(path, continuation, token);
            pages.Add(page.Content);
            Assert.Equal(pageSize, page.Content.Length);
            continuation = page.Continuation;
            if (continuation is not null)
            {
                Assert.Equal((long)pages.Count * pageSize + 2, continuation.ByteOffset);
            }
        } while (continuation is not null);

        Assert.Equal(64, pages.Count);
        Assert.Equal(content, string.Concat(pages));
    }

    [Fact]
    public async Task ReadPageAsync_Continuation_StartsReadingAtItsByteOffset()
    {
        var token = TestContext.Current.CancellationToken;
        var bytes = Encoding.UTF8.GetBytes("\uFFFD" + new string('a', 32 * 1024));
        var path = await WriteFileAsync("direct-seek.txt", bytes, token);
        var first = await ReadPageAsync(path, null, token);
        var next = Assert.IsType<FileReadContinuation>(first.Continuation);
        await using var stream = new ObservedFileStream(path);

        var page = await ReadPageAsync(stream, next, token);

        Assert.NotEmpty(page.Content);
        Assert.Equal(next.ByteOffset, stream.FirstReadOffset);
    }

    [Fact]
    public async Task ReadPageAsync_Utf8LookaheadEndsInsideNextScalar_PreservesTheEntireText()
    {
        var token = TestContext.Current.CancellationToken;
        var content = new string('界', 8191) + "\uFFFD😀末";
        var path = await WriteFileAsync("lookahead.txt", Encoding.UTF8.GetBytes(content), token);

        var page = await ReadPageAsync(path, null, token);

        Assert.Equal(content, page.Content);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public async Task ReadPageAsync_InvalidUtf8BeforePageBoundary_ResumesBeforeNextLeadByte()
    {
        var token = TestContext.Current.CancellationToken;
        var prefix = Encoding.UTF8.GetBytes(
            new string('a', ProjectAgentFileReadPager.MaximumPageContentCharacters - 1)
        );
        var bytes = prefix.Concat(new byte[] { 0xE2, 0xC2, 0xA9 }).ToArray();
        var path = await WriteFileAsync("invalid-utf8.txt", bytes, token);

        var first = await ReadPageAsync(path, null, token);
        var next = Assert.IsType<FileReadContinuation>(first.Continuation);
        var last = await ReadPageAsync(path, next, token);

        Assert.EndsWith("\uFFFD", first.Content, StringComparison.Ordinal);
        Assert.Equal(prefix.Length + 1, next.ByteOffset);
        Assert.Equal("©", last.Content);
        Assert.Null(last.Continuation);
    }

    [Fact]
    public async Task ReadPageAsync_InvalidUtf16SurrogateBeforePageBoundary_ResumesBeforeNextCodeUnit()
    {
        var token = TestContext.Current.CancellationToken;
        var prefix = Encoding.Unicode.GetBytes(
            new string('a', ProjectAgentFileReadPager.MaximumPageContentCharacters - 1)
        );
        var bytes = Encoding
            .Unicode.GetPreamble()
            .Concat(prefix)
            .Concat(new byte[] { 0x00, 0xD8, 0x3D, 0xD8, 0x00, 0xDE })
            .ToArray();
        var path = await WriteFileAsync("invalid-utf16.txt", bytes, token);

        var first = await ReadPageAsync(path, null, token);
        var next = Assert.IsType<FileReadContinuation>(first.Continuation);
        var last = await ReadPageAsync(path, next, token);

        Assert.EndsWith("\uFFFD", first.Content, StringComparison.Ordinal);
        Assert.Equal(Encoding.Unicode.GetPreamble().Length + prefix.Length + 2, next.ByteOffset);
        Assert.Equal("😀", last.Content);
        Assert.Null(last.Continuation);
    }

    [Fact]
    public async Task ReadPageAsync_InvalidUtf32ScalarBeforePageBoundary_ResumesAtNextScalar()
    {
        var token = TestContext.Current.CancellationToken;
        var prefix = Encoding.UTF32.GetBytes(
            new string('a', ProjectAgentFileReadPager.MaximumPageContentCharacters - 1)
        );
        var bytes = Encoding
            .UTF32.GetPreamble()
            .Concat(prefix)
            .Concat(new byte[] { 0x00, 0xD8, 0x00, 0x00, (byte)'B', 0x00, 0x00, 0x00 })
            .ToArray();
        var path = await WriteFileAsync("invalid-utf32.txt", bytes, token);

        var first = await ReadPageAsync(path, null, token);
        var next = Assert.IsType<FileReadContinuation>(first.Continuation);
        var last = await ReadPageAsync(path, next, token);

        Assert.EndsWith("\uFFFD", first.Content, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF32.GetPreamble().Length + prefix.Length + 4, next.ByteOffset);
        Assert.Equal("B", last.Content);
        Assert.Null(last.Continuation);
    }

    private async Task<string> WriteFileAsync(string fileName, byte[] contents, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(path, contents, cancellationToken);
        return path;
    }

    private static async Task<ProjectAgentFileReadPage> ReadPageAsync(
        string path,
        FileReadContinuation? continuation,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await ReadPageAsync(stream, continuation, cancellationToken);
    }

    private static async Task<ProjectAgentFileReadPage> ReadPageAsync(
        Stream stream,
        FileReadContinuation? continuation,
        CancellationToken cancellationToken
    )
    {
        using var reader = ProjectAgentFileReadPager.OpenReader(stream, continuation?.CodePage);
        var options = new ProjectAgentFileReadOptions(StartLine: 1, EndLine: null, NumberedLines: false);
        var position = continuation ?? FileReadContinuation.Start(reader.CurrentEncoding);
        return await ProjectAgentFileReadPager.ReadPageAsync(reader, options, position, cancellationToken);
    }

    private sealed class ObservedFileStream(string path)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        public long? FirstReadOffset { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            FirstReadOffset ??= Position;
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FirstReadOffset ??= Position;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
