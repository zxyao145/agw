using System.IO.Pipelines;
using System.Text;
using Agw.Agents.Execution.Agents.Runtime;

namespace Agw.Agents.Tests;

public class CancellationBoundStreamTests
{
    [Fact]
    public async Task ReadLineAsync_BoundTokenCanceledWhileWaitingForData_ThrowsOperationCanceled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var pipe = new Pipe();
        using var bound = new CancellationTokenSource();
        using var reader = new StreamReader(new CancellationBoundStream(pipe.Reader.AsStream(), bound.Token));
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("{\"done\":false}\n"), cancellationToken);

        // 与 OllamaSharp 相同，逐行读取时不传入令牌。
        // Like OllamaSharp, read lines without passing a token.
        Assert.Equal("{\"done\":false}", await reader.ReadLineAsync());
        var waiting = reader.ReadLineAsync();
        await Task.Delay(100, cancellationToken);
        Assert.False(waiting.IsCompleted);

        await bound.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task ReadAsync_CallerTokenCanceledWhileWaitingForData_ThrowsOperationCanceled()
    {
        var pipe = new Pipe();
        await using var stream = new CancellationBoundStream(pipe.Reader.AsStream(), CancellationToken.None);
        using var caller = new CancellationTokenSource();

        var waiting = stream.ReadAsync(new byte[16], caller.Token).AsTask();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}
