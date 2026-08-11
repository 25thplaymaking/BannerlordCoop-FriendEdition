using System.Net.Sockets;

namespace CoopLauncher.Services;

/// <summary>
/// Liveness check for the co-op host. The server speaks LiteNetLib over <b>UDP</b> (there is no TCP
/// listener), so this can't be a TCP connect. It also can't rely on ICMP "port unreachable": the host's
/// firewall silently drops datagrams to closed ports rather than rejecting them, so a closed port looks
/// identical to an open-but-silent one. The one reliable signal is that a live co-op server <i>answers</i>
/// a stray datagram with a UDP datagram of its own. So: send a small packet and treat a reply as online;
/// silence (or a reset) across a few tries as offline.
/// </summary>
public static class ServerProbe
{
    public static async Task<bool> IsOnlineAsync(string host, int port, int perTryMs = 900, int tries = 3)
    {
        for (int attempt = 0; attempt < tries; attempt++)
        {
            if (await GotReplyAsync(host, port, perTryMs))
                return true;
        }
        return false;
    }

    private static async Task<bool> GotReplyAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var udp = new UdpClient(host.Contains(':')
                ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
            udp.Connect(host, port);

            await udp.SendAsync(new byte[] { 0 }, 1);

            var receive = udp.ReceiveAsync();
            var finished = await Task.WhenAny(receive, Task.Delay(timeoutMs));
            if (finished != receive) return false;   // silence within the window → no live server (this try)

            await receive;                            // a datagram came back → the co-op server answered
            return true;
        }
        catch (SocketException)
        {
            return false;                             // reset / DNS / route error → treat as not reachable
        }
        catch
        {
            return false;
        }
    }
}
