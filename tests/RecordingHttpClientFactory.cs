using Microsoft.Extensions.DependencyInjection;

namespace Agw.Testing;

/// <summary>
/// <para>包装实际的 IHttpClientFactory，并记录创建的 HttpClient，供测试检查地址、认证头及释放状态。</para>
/// <para>Wraps the real IHttpClientFactory and records created HttpClients so tests can inspect address, auth header, and disposal.</para>
/// </summary>
internal sealed class RecordingHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IHttpClientFactory _inner;

    public RecordingHttpClientFactory()
    {
        _services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        _inner = _services.GetRequiredService<IHttpClientFactory>();
    }

    public List<HttpClient> Clients { get; } = [];

    public HttpClient CreateClient(string name)
    {
        var client = _inner.CreateClient(name);
        Clients.Add(client);
        return client;
    }

    public void Dispose() => _services.Dispose();
}
