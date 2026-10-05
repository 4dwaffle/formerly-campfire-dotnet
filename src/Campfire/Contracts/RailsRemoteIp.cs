using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Campfire.Contracts;

// ActionDispatch::RemoteIp removes private proxy addresses from the reversed
// forwarded chain and checks conflicting Client-Ip headers. Resolve lazily,
// just as Rails does, and retain the headers before ASP.NET consumes them.
public static class RailsRemoteIp
{
    private sealed record Headers(string Forwarded, string StandardForwarded, string Client, IPAddress? Remote);
    private sealed record AddressValue(string Text, IPAddress Ip);
    public static async Task Capture(HttpContext context, RequestDelegate next)
    {
        context.Items[typeof(Headers)] = ReadHeaders(context);
        await next(context);
    }
    public static string? Address(HttpContext context)
    {
        var headers = context.Items[typeof(Headers)] as Headers ?? ReadHeaders(context);
        var standard = Regex.Matches(headers.StandardForwarded, "(?:^|[,;])\\s*for=(?:\"([^\"]*)\"|([^,;\\s]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
        var forwarded = Parse(standard.Length > 0 ? string.Join(',', standard) : headers.Forwarded, authority: true).Reverse().ToArray();
        var client = Parse(headers.Client).Reverse().ToArray();
        if (client.Length > 0 && forwarded.Length > 0 && !forwarded.Any(ip => ip.Text == client[^1].Text))
            throw new InvalidOperationException("Conflicting Client-Ip and X-Forwarded-For headers.");
        var chain = forwarded.Concat(client).ToArray();
        return chain.Concat(headers.Remote is null ? [] : new[] { new AddressValue(headers.Remote.ToString(), headers.Remote) }).FirstOrDefault(ip => !Trusted(ip.Ip))?.Text
            ?? chain.LastOrDefault()?.Text ?? headers.Remote?.ToString();
    }
    private static Headers ReadHeaders(HttpContext context) => new(context.Request.Headers["X-Forwarded-For"].ToString(), context.Request.Headers["Forwarded"].ToString(), context.Request.Headers["Client-Ip"].ToString(), context.Connection.RemoteIpAddress);
    private static IEnumerable<AddressValue> Parse(string header, bool authority = false)
    {
        foreach (var part in Regex.Split(header.Trim(), @"[,\s]+", RegexOptions.CultureInvariant))
        {
            var host = part;
            if (authority && host.StartsWith('[') && host.IndexOf(']') is var end && end > 0) host = host[1..end];
            else if (authority && host.Count(c => c == ':') == 1) host = host.Split(':')[0];
            if (!host.Contains('/') && IPAddress.TryParse(host, out var address) && (address.AddressFamily != AddressFamily.InterNetwork || host == address.ToString())) yield return new AddressValue(host, address);
        }
    }
    private static bool Trusted(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
            : (bytes[0] & 0xfe) == 0xfc || bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80;
    }
}
