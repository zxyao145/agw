using PiAgentSdk.Internal;
using Xunit;

namespace PiAgentSdk.Tests;

public sealed class PiProcessTransportTests
{
    [Fact]
    public async Task DisposeAsync_WithWriteInProgress_CancelsWriteBeforeReleasingResources()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        await using var script = await TemporaryScript.CreateAsync("sleep 30", TestContext.Current.CancellationToken);
        await using var transport = CreateTransport(script.Path);
        await transport.StartAsync(TestContext.Current.CancellationToken);
        var writeTask = transport
            .WriteLineAsync(new string('x', 4 * 1024 * 1024), TestContext.Current.CancellationToken)
            .AsTask();
        var firstCompleted = await Task.WhenAny(
            writeTask,
            Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)
        );
        Assert.NotSame(writeTask, firstCompleted);

        // Act
        await transport
            .DisposeAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var writeException = await Record.ExceptionAsync(async () => await writeTask);

        // Assert
        Assert.NotNull(writeException);
        Assert.IsNotType<ObjectDisposedException>(writeException);
    }

    [Fact]
    public async Task KillAsync_AfterProcessExit_IsIdempotent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        await using var script = await TemporaryScript.CreateAsync("exit 0", TestContext.Current.CancellationToken);
        await using var transport = CreateTransport(script.Path);
        await transport.StartAsync(TestContext.Current.CancellationToken);
        _ = await transport.WaitForExitAsync(TestContext.Current.CancellationToken);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await transport.KillAsync(TestContext.Current.CancellationToken)
        );

        // Assert
        Assert.Null(exception);
    }

    private static PiProcessTransport CreateTransport(string executable) =>
        new(
            new PiAgentOptions { PiPathOverride = executable, AbortGracePeriod = TimeSpan.FromSeconds(2) },
            new PiSessionOptions(),
            resumeSessionId: null,
            logger: null
        );

    private sealed class TemporaryScript : IAsyncDisposable
    {
        private TemporaryScript(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }

        public string Path { get; }

        public static async Task<TemporaryScript> CreateAsync(string body, CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("The process transport script fixture requires a Unix shell.");
            }

            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pi-agent-sdk-{Guid.CreateVersion7():N}"
            );
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "pi-test-script");
            await File.WriteAllTextAsync(path, $"#!/bin/sh\n{body}\n", cancellationToken);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return new TemporaryScript(directory, path);
        }

        public ValueTask DisposeAsync()
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
