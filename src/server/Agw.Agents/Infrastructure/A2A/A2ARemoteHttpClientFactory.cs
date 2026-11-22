using System.Net.Sockets;
using Agw.Agents.Application.A2A;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Net;

namespace Agw.Agents.Infrastructure.A2A;

internal sealed class A2ARemoteHttpClientFactory : IA2ARemoteHttpClientFactory
{
    public const string ClientName = "a2a-remote";
    private static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(5);
    private readonly IHttpClientFactory _clients;

    public A2ARemoteHttpClientFactory(IHttpClientFactory clients)
    {
        _clients = clients;
    }

    public A2ARemoteHttpClient Create(Uri targetUri, A2AEndpointCredentials credentials) =>
        new(_clients.CreateClient(ClientName), targetUri, credentials);

    public static SocketsHttpHandler CreateTransport(bool allowPrivateNetworks)
    {
        var transport = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = PooledConnectionLifetime,
        };
        if (!allowPrivateNetworks)
        {
            transport.UseProxy = false;
            transport.ConnectCallback = ConnectPublicAsync;
        }
        return transport;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken
    )
    {
        var addresses = await PrivateNetworkAddresses.ResolveAsync(
            context.DnsEndPoint.Host,
            allowPrivateNetworks: false,
            "A2A endpoint resolves to a prohibited address.",
            cancellationToken
        );

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
