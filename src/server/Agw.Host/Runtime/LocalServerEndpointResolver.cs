using System.Net;
using System.Net.Sockets;

namespace Agw.Host.Runtime;

public static class LocalServerEndpointResolver
{
    public const int DefaultPort = 30816;

    public static string ResolveDefaultUrl()
    {
        return ResolveDefaultUrl(DefaultPort, IsPortAvailable, AllocateAvailablePort);
    }

    public static string ResolveDefaultUrl(
        int preferredPort,
        Func<int, bool> isPortAvailable,
        Func<int> allocateAvailablePort)
    {
        var port = isPortAvailable(preferredPort) ? preferredPort : allocateAvailablePort();
        return $"http://127.0.0.1:{port}";
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int AllocateAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
