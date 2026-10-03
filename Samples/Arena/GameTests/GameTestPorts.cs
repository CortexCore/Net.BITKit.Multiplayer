using System.Net;
using System.Net.Sockets;

namespace BITKit.Multiplayer.Samples.Arena;

internal static class GameTestPorts
{
    // Select a port where UDP and TCP can BOTH bind, holding the UDP probe while
    // checking TCP. Release both before the real room/server binds (TOCTOU remains).
    internal static int FreeTcpUdpPort()
    {
        SocketException? failure = null;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            try
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)udp.LocalEndPoint!).Port;
                using var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                return port;
            }
            catch (SocketException ex) { failure = ex; } // Probe only: never retry a failed game test.
        }
        throw new InvalidOperationException("Cannot reserve a loopback port available to both TCP and UDP.", failure);
    }
}
