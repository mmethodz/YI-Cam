using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace YiLocal.Core;

public sealed record DiscoveredCamera(string Ip, string Uid)
{
    public override string ToString() => Ip;
}

/// <summary>Three local TNP discovery broadcasts; no login, key, subnet sweep or persistent camera change.</summary>
public static class CameraDiscovery
{
    internal static IPAddress LanAddress(string text)
    {
        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException(L.Get("EnterTheCameraSPrivateIPv4Address"));
        var b = address.GetAddressBytes();
        if (!(b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 127))
            throw new ArgumentException(L.Get("OnlyAPrivateLANCameraAddressIsAccepted"));
        return address;
    }
    internal static DiscoveredCamera? ParseReply(ReadOnlySpan<byte> data, IPAddress address)
    {
        if (data.Length < 24 || data[0] != 0xf1 || data[1] != 0x41 || BinaryPrimitives.ReadUInt16BigEndian(data[2..]) != data.Length - 4) return null;
        try { LanAddress(address.ToString()); } catch (ArgumentException) { return null; }
        if (data[4] != 'T' || data[5] != 'N' || data[6] != 'P') return null;
        return new(address.ToString(), Convert.ToHexString(data.Slice(4, 20)).ToLowerInvariant());
    }
    internal static IReadOnlyList<(IPAddress Address, IPAddress Broadcast)> Interfaces()
    {
        var result = new List<(IPAddress, IPAddress)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                     n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
            foreach (var item in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                try { LanAddress(item.Address.ToString()); } catch (ArgumentException) { continue; }
                byte[] a = item.Address.GetAddressBytes(), m = item.IPv4Mask.GetAddressBytes();
                if (m.Length != 4 || m.All(b => b == 255) || m.All(b => b == 0)) continue;
                result.Add((item.Address, new IPAddress(a.Zip(m, (x, y) => (byte)(x | ~y)).ToArray())));
            }
        return result.Distinct().ToArray();
    }
    public static async Task<IReadOnlyList<DiscoveredCamera>> FindAsync(CancellationToken cancellation = default)
    {
        var results = await Task.WhenAll(Interfaces().Select(async network =>
        {
            var found = new Dictionary<string, DiscoveredCamera>();
            using var udp = new UdpClient(new IPEndPoint(network.Address, 0)) { EnableBroadcast = true };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await udp.SendAsync(new byte[] { 0xf1, 0x30, 0, 0 }, new IPEndPoint(network.Broadcast, 32108), timeout.Token);
                    using var window = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    window.CancelAfter(TimeSpan.FromMilliseconds(800));
                    try
                    {
                        while (true)
                        {
                            var reply = await udp.ReceiveAsync(window.Token);
                            if (ParseReply(reply.Buffer, reply.RemoteEndPoint.Address) is { } camera) found[camera.Ip + "/" + camera.Uid] = camera;
                        }
                    }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            catch (SocketException) { /* An unavailable adapter must not hide answers on another adapter. */ }
            return found.Values.ToArray();
        }));
        cancellation.ThrowIfCancellationRequested();
        return results.SelectMany(r => r).Distinct().OrderBy(r => r.Ip, StringComparer.Ordinal).ToArray();
    }
}
