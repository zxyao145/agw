using System.Text;
using PiAgentSdk.Internal;
using Xunit;

namespace PiAgentSdk.Tests;

public sealed class JsonlReaderTests
{
    [Fact]
    public async Task ReadLinesAsync_CrLfAndUnicodeSeparators_SplitsOnlyOnLf()
    {
        // Arrange
        var bytes = Encoding.UTF8.GetBytes("{\"value\":\"a\u2028b\u2029c\"}\r\n{\"value\":2}\n");
        await using var stream = new ChunkedStream(bytes, 3);

        // Act
        var lines = new List<string>();
        await foreach (
            var line in PiJsonlReader.ReadLinesAsync(stream, cancellationToken: TestContext.Current.CancellationToken)
        )
        {
            lines.Add(line);
        }

        // Assert
        Assert.Equal(2, lines.Count);
        Assert.Contains("\u2028", lines[0]);
        Assert.False(lines[0].EndsWith('\r'));
        Assert.Equal("{\"value\":2}", lines[1]);
    }

    [Fact]
    public async Task ReadLinesAsync_OversizedRecord_ThrowsProtocolException()
    {
        // Arrange
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("12345"));

        // Act
        var exception = await Assert.ThrowsAsync<PiProtocolException>(async () =>
        {
            await foreach (
                var _ in PiJsonlReader.ReadLinesAsync(
                    stream,
                    maximumRecordBytes: 4,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            ) { }
        });

        // Assert
        Assert.Contains("exceeded", exception.Message);
    }

    private sealed class ChunkedStream : MemoryStream
    {
        private readonly int _chunkSize;

        public ChunkedStream(byte[] buffer, int chunkSize)
            : base(buffer)
        {
            _chunkSize = chunkSize;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], cancellationToken);
    }
}
