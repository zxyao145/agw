using System.Collections.ObjectModel;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;

namespace Agw.Agents.Application.A2A;

public sealed class A2ARemoteHttpClient : IDisposable
{
    private static readonly AsyncLocal<Request?> ActiveRequest = new();

    internal A2ARemoteHttpClient(HttpClient client, Uri target, A2AEndpointCredentials credentials)
    {
        Client = client;
        Target = target;
        Origin = A2AEndpointRules.NormalizeOrigin(target);
        Headers = credentials.HeadersByOrigin.GetValueOrDefault(Origin) ?? ReadOnlyDictionary<string, string>.Empty;
    }

    public HttpClient Client { get; }
    public Uri Target { get; }
    internal string Origin { get; }
    internal IReadOnlyDictionary<string, string> Headers { get; }
    internal static Request? CurrentRequest => ActiveRequest.Value;

    public ValueTask<T> InvokeAsync<T>(Func<Task<T>> send) => InvokeAsync(() => new ValueTask<T>(send()));

    public async ValueTask<T> InvokeAsync<T>(
        Func<ValueTask<T>> send,
        Func<HttpResponseMessage, CancellationToken, ValueTask>? onResponse = null
    )
    {
        // 凭据仅在当前 SDK 调用的执行上下文中使用。
        // Credentials are available only in the execution context of the current SDK call.
        var previous = ActiveRequest.Value;
        ActiveRequest.Value = new Request(this, onResponse);
        try
        {
            return await send().ConfigureAwait(false);
        }
        finally
        {
            ActiveRequest.Value = previous;
        }
    }

    public void Dispose() => Client.Dispose();

    internal sealed record Request(
        A2ARemoteHttpClient Target,
        Func<HttpResponseMessage, CancellationToken, ValueTask>? OnResponse
    );
}
