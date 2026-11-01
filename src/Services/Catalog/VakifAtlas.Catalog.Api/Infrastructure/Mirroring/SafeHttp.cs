using System.Net;
using System.Net.Sockets;

namespace VakifAtlas.Catalog.Api.Infrastructure.Mirroring;

// outbound fetch of staff-provided image URLs (SSRF guard): https on port 443 only, and only public addresses.
// The address is checked on the connection actually opened, so neither redirects nor DNS rebinding can reach
// internal hosts
public static class SafeHttp
{
    public static bool IsAllowedUrl(string? url) =>
        url is { Length: <= 2000 } &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.HostNameType == UriHostNameType.Dns;

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = true, // https → http downgrades are never followed
        MaxAutomaticRedirections = 3,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var endpoint = context.DnsEndPoint;
            if (endpoint.Port != 443)
            {
                throw new HttpRequestException($"Port {endpoint.Port} is not allowed.");
            }

            var addresses = (await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken)).Where(IsPublic).ToArray();
            if (addresses.Length == 0)
            {
                throw new HttpRequestException($"{endpoint.Host} does not resolve to a public address.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224 ||
                     (b[0] == 100 && b[1] is >= 64 and <= 127) ||  // carrier-grade NAT
                     (b[0] == 169 && b[1] == 254) ||               // link-local, cloud metadata
                     (b[0] == 172 && b[1] is >= 16 and <= 31) ||
                     (b[0] == 192 && b[1] == 0 && b[2] == 0) ||
                     (b[0] == 192 && b[1] == 168) ||
                     (b[0] == 198 && b[1] is 18 or 19));           // benchmarking
        }

        var bytes = address.GetAddressBytes();
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                 (bytes[0] & 0xFE) == 0xFC); // unique local fc00::/7
    }
}
