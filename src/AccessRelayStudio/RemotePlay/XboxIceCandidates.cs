using System.Buffers.Binary;
using System.Net;
using System.Text.Json;

namespace EliteScriptStudio.RemotePlay;

internal static class XboxIceCandidates
{
    private const int HomePort = 9002;

    internal static IReadOnlyList<IPEndPoint> ConsoleEndpoints(JsonElement configuration)
    {
        if (configuration.ValueKind != JsonValueKind.Object || !configuration.TryGetProperty("serverDetails", out var server)
            || server.ValueKind != JsonValueKind.Object) return [];

        HashSet<IPEndPoint> endpoints = [];
        foreach (var (hostKey, portKey) in new[] { ("ipAddress", "port"), ("ipV4Address", "ipV4Port"), ("ipV6Address", "ipV6Port") })
        {
            if (!server.TryGetProperty(hostKey, out var host) || host.ValueKind != JsonValueKind.String
                || !IPAddress.TryParse(host.GetString(), out var address) || !IsUnicast(address)) continue;
            if (server.TryGetProperty(portKey, out var port) && int.TryParse(port.ToString(), out int number) && number is > 0 and <= 65535)
                endpoints.Add(new(address, number));
            endpoints.Add(new(address, HomePort));
        }
        return endpoints.ToArray();
    }

    internal static XboxIce[] ForPeer(IEnumerable<XboxIce> exchanged, IReadOnlyList<IPEndPoint> consoleEndpoints)
    {
        List<XboxIce> candidates = [];
        HashSet<XboxIce> seen = [], endings = [];
        HashSet<(int Index, IPEndPoint Endpoint)> advertised = [];
        List<(XboxIce Media, IPEndPoint Endpoint)> fallbacks = [];
        foreach (var item in exchanged)
        {
            if (item.SdpMLineIndex is < 0 or >= 32) continue;
            string line = item.Candidate.Trim();
            if (line.StartsWith("a=", StringComparison.Ordinal)) line = line[2..];
            var candidate = item with { Candidate = line };
            if (line == "end-of-candidates" || line.Length == 0) { endings.Add(candidate with { Candidate = "" }); continue; }
            if (!line.StartsWith("candidate:", StringComparison.Ordinal) || !seen.Add(candidate)) continue;
            candidates.Add(candidate);

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8 || parts[1] != "1" || !parts[2].Equals("UDP", StringComparison.OrdinalIgnoreCase)
                || !IPAddress.TryParse(parts[4], out var address) || !ushort.TryParse(parts[5], out var port) || port == 0) continue;
            advertised.Add((item.SdpMLineIndex, new(address, port)));
            if (!address.IsIPv6Teredo) continue;

            // RFC 4380 section 4 encodes the public IPv4 address and UDP port in the last six bytes.
            var bytes = address.GetAddressBytes();
            ushort mappedPort = (ushort)~BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10));
            for (int i = 12; i < 16; i++) bytes[i] = (byte)~bytes[i];
            var v4 = new IPAddress(bytes.AsSpan(12));
            if (!IsUnicast(v4)) continue;
            if (mappedPort > 0) fallbacks.Add((item, new(v4, mappedPort)));
            fallbacks.Add((item, new(v4, HomePort)));
        }

        // The offer uses max-bundle. The first media section carries the shared transport.
        var bundle = candidates.FirstOrDefault(c => c.SdpMLineIndex == 0) ?? new XboxIce("", 0, null);
        foreach (var endpoint in consoleEndpoints) fallbacks.Add((bundle, endpoint));
        int foundation = 0;
        foreach (var (media, endpoint) in fallbacks)
        {
            if (!advertised.Add((media.SdpMLineIndex, endpoint))) continue;
            candidates.Add(media with { Candidate = $"candidate:ar{++foundation} 1 UDP 255 {endpoint.Address} {endpoint.Port} typ host" });
        }
        candidates.AddRange(endings);
        return candidates.ToArray();
    }

    private static bool IsUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? address.GetAddressBytes()[0] is > 0 and < 224 : !address.IsIPv6Multicast;
    }
}
