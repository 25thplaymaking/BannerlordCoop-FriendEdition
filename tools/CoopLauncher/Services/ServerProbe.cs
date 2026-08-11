using System.Net.Sockets;

namespace CoopLauncher.Services;

/// <summary>
/// A cheap TCP reachability check against the co-op port so the banner can show whether the
/// campaign is actually up before a friend commits to loading in.
/// </summary>
public static class ServerProbe
{
    public static async Task<bool> IsOnlineAsync(string host, int port, int timeoutMs = 2500)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeoutMs));
            if (done != connect) return false;      // timed out
            await connect;                            // surface a refused/failed connect as offline
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
