using System.Reflection;
using Agw.Agents.Execution.Durable;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agw.Agents.Tests;

public sealed partial class DurableExecutionStoreTests
{
    [Theory]
    [InlineData("stream ID is equal or smaller than the last ID")]
    [InlineData("stream position rejected")]
    public async Task RedisBatch_PipelinesOrderedWrites_VerifiesDuplicatesAndRenewsTtlOncePerBatch(
        string duplicateError
    )
    {
        var fake = new RedisBatchFake { DuplicateError = duplicateError };
        var stream = new RedisExecutionEventStream(fake.Connection, Options.Create(new ExecutionRuntimeOptions()));
        var id = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        await stream.AppendBatchAsync(id, 0, [new(1, BatchText("one")), new(0, BatchText("zero"))], token);
        var original = fake.Payloads["1-1"];
        await stream.AppendBatchAsync(id, 0, [new(1, BatchText("replacement")), new(2, BatchText("two"))], token);

        Assert.Equal(["1-0", "1-1", "1-1", "1-2"], fake.ExecutedIds);
        Assert.Equal(2, fake.Executions);
        Assert.Equal(2, fake.TtlRenewals);
        Assert.Equal(original, fake.Payloads["1-1"]);
        Assert.Equal(3, fake.Payloads.Count);
    }

    [Fact]
    public async Task RedisBatch_UnrelatedServerFailureWithoutStoredPosition_RemainsUnavailable()
    {
        // Arrange
        var fake = new RedisBatchFake { AppendError = "READONLY replica cannot accept writes" };
        var stream = new RedisExecutionEventStream(fake.Connection, Options.Create(new ExecutionRuntimeOptions()));

        // Act
        var error = await Assert.ThrowsAsync<AgwException>(() =>
            stream
                .AppendAsync(Guid.NewGuid(), 0, 0, BatchText("pending"), TestContext.Current.CancellationToken)
                .AsTask()
        );

        // Assert
        Assert.Equal(ErrorCodes.DurableExecutionUnavailable.Code, error.Code);
        Assert.Empty(fake.Payloads);
        Assert.Equal(0, fake.TtlRenewals);
    }

    [Fact]
    public async Task RedisBatch_MissingEarlierPosition_IsNotMistakenForSuccessfulReplay()
    {
        var fake = new RedisBatchFake();
        var stream = new RedisExecutionEventStream(fake.Connection, Options.Create(new ExecutionRuntimeOptions()));
        var id = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        await stream.AppendAsync(id, 0, 2, BatchText("two"), token);

        var error = await Assert.ThrowsAsync<AgwException>(() =>
            stream.AppendBatchAsync(id, 0, [new(1, BatchText("missing")), new(3, BatchText("three"))], token).AsTask()
        );

        Assert.Equal(ErrorCodes.DurableExecutionConflict.Code, error.Code);
        Assert.False(fake.Payloads.ContainsKey("1-1"));
        Assert.True(fake.Payloads.ContainsKey("1-3"));
    }

    private sealed class RedisBatchFake
    {
        public Dictionary<string, string> Payloads { get; } = [];
        public List<string> ExecutedIds { get; } = [];
        public int Executions { get; private set; }
        public int TtlRenewals { get; private set; }
        public IConnectionMultiplexer Connection { get; }
        public string DuplicateError { get; init; } = "stream ID is equal or smaller than the last ID";
        public string? AppendError { get; init; }
        private ulong? _last;

        public RedisBatchFake()
        {
            var database = RedisDispatch.Create<IDatabase>(
                (method, args) =>
                    method switch
                    {
                        "CreateBatch" => CreateBatch(),
                        "StreamRangeAsync" => Task.FromResult(
                            Payloads
                                .Where(entry =>
                                    ulong.Parse(entry.Key.Split('-')[1])
                                    >= ulong.Parse(args[1]!.ToString()!.Split('-')[1])
                                )
                                .OrderBy(entry => ulong.Parse(entry.Key.Split('-')[1]))
                                .Take(1)
                                .Select(entry => new StreamEntry(
                                    entry.Key,
                                    [new NameValueEntry("payload", entry.Value)]
                                ))
                                .ToArray()
                        ),
                        "KeyExpireAsync" => RenewTtl(),
                        _ => throw new NotSupportedException(method),
                    }
            );
            Connection = RedisDispatch.Create<IConnectionMultiplexer>(
                (method, _) => method == "GetDatabase" ? database : throw new NotSupportedException(method)
            );
        }

        private Task<bool> RenewTtl()
        {
            TtlRenewals++;
            return Task.FromResult(true);
        }

        private IBatch CreateBatch()
        {
            var pending = new List<Action>();
            return RedisDispatch.Create<IBatch>(
                (method, args) =>
                {
                    if (method == "StreamAddAsync")
                    {
                        var id = args[2]!.ToString()!;
                        var values = (NameValueEntry[])args[1]!;
                        var result = new TaskCompletionSource<RedisValue>(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        );
                        pending.Add(() =>
                        {
                            ExecutedIds.Add(id);
                            var sequence = ulong.Parse(id.Split('-')[1]);
                            if (AppendError != null)
                                result.SetException(new RedisServerException(AppendError));
                            else if (_last.HasValue && sequence <= _last.Value)
                                result.SetException(new RedisServerException(DuplicateError));
                            else
                            {
                                _last = sequence;
                                Payloads[id] = values[0].Value.ToString();
                                result.SetResult(id);
                            }
                        });
                        return result.Task;
                    }
                    if (method == "Execute")
                    {
                        Executions++;
                        foreach (var write in pending)
                            write();
                        return null;
                    }
                    throw new NotSupportedException(method);
                }
            );
        }
    }

    public class RedisDispatch : DispatchProxy
    {
        private Func<string, object?[], object?> _invoke = null!;

        public static T Create<T>(Func<string, object?[], object?> invoke)
            where T : class
        {
            var proxy = Create<T, RedisDispatch>();
            ((RedisDispatch)(object)proxy)._invoke = invoke;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            _invoke(targetMethod!.Name, args ?? []);
    }
}
