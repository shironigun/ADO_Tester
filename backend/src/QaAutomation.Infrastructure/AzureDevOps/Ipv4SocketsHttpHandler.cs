using System.Net;
using System.Net.Sockets;

namespace QaAutomation.Infrastructure.AzureDevOps;

/// <summary>
/// Builds a <see cref="SocketsHttpHandler"/> that connects over IPv4 only. On some Windows networks
/// the IPv6 path to <c>dev.azure.com</c> hangs until <see cref="HttpClient.Timeout"/>, while IPv4
/// succeeds in ~1–3s (curl / dual-stack happy-eyeballs disagree with .NET's default connect).
/// </summary>
internal static class Ipv4SocketsHttpHandler
{
    public static SocketsHttpHandler Create() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(
                context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
            if (addresses.Length == 0)
                throw new SocketException((int)SocketError.HostNotFound);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
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
        },
    };
}
