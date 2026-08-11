using CoopLauncher.Services;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace CoopLauncher.Tests;

public sealed class ServerProbeTests
{
    [Fact]
    public async Task UdpReply_MarksServerOnline()
    {
        using var responder = new UdpClient(AddressFamily.InterNetworkV6);
        responder.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        int port = ((IPEndPoint)responder.Client.LocalEndPoint!).Port;

        Task reply = Task.Run(async () =>
        {
            UdpReceiveResult request = await responder.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await responder.SendAsync(new byte[] { 1 }, request.RemoteEndPoint);
        });

        Assert.True(await ServerProbe.IsOnlineAsync("::1", port, perTryMs: 500, tries: 1));
        await reply;
    }

    [Fact]
    public async Task UdpSilence_MarksServerOffline()
    {
        using var silentListener = new UdpClient(AddressFamily.InterNetworkV6);
        silentListener.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        int port = ((IPEndPoint)silentListener.Client.LocalEndPoint!).Port;

        Assert.False(await ServerProbe.IsOnlineAsync("::1", port, perTryMs: 75, tries: 1));
    }
}
