using System.Net;
using System.Net.Sockets;

namespace Campfire.Features.Integrations;

public static class OutboundPolicy
{
    private static readonly string[] PushHosts = ["jmt17.google.com", "fcm.googleapis.com", "updates.push.services.mozilla.com", "web.push.apple.com", "notify.windows.com"];
    public static bool PushUri(string? endpoint, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" || parsed.Port != 443) return false;
        if (!PushHosts.Any(host => parsed.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase) || parsed.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase))) return false;
        uri = parsed; return true;
    }
    public static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is not (0 or 10 or 127) && !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) && !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) && bytes[0] < 224 && !(bytes[0] == 198 && bytes[1] is 18 or 19) && !(bytes[0] == 192 && bytes[1] == 0) && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
        // Public push services do not use link-local, unique-local, multicast, documentation,
        // IPv4-compatible or transition ranges. Accept only ordinary global unicast.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) && !(bytes[0] == 0x20 && bytes[1] == 0x02) && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0);
    }
    public static async Task<IPAddress?> ResolvePush(Uri uri, CancellationToken cancellationToken)
    {
        if (!PushUri(uri.AbsoluteUri, out _)) return null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.IdnHost, cancellationToken);
            return addresses.FirstOrDefault(PublicAddress);
        }
        catch (SocketException) { return null; }
    }
    public static SocketsHttpHandler PinnedHandler(IPAddress address) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    };
}
