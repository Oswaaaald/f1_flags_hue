using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace F1Hue.Infrastructure;

public static class HueDiscovery
{
    public static async Task<string[]> DiscoverAsync(CancellationToken ct)
    {
        var found = new HashSet<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var query = new List<byte>(new byte[12]);
            query[5] = 1;
            foreach (var label in "_hue._tcp.local".Split('.'))
            {
                query.Add((byte)label.Length);
                query.AddRange(Encoding.ASCII.GetBytes(label));
            }
            query.AddRange([0, 0, 12, 128, 1]); // PTR, request a unicast response.
            await socket.SendAsync(query.ToArray(), new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), deadline.Token);
            while (!deadline.IsCancellationRequested)
            {
                var packet = await socket.ReceiveAsync(deadline.Token);
                foreach (var address in ParseAddresses(packet.Buffer))
                    found.Add(address);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (SocketException) { }
        if (found.Count == 0)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                var result = await http.GetFromJsonAsync<System.Text.Json.JsonElement>("https://discovery.meethue.com/", ct);
                foreach (var bridge in result.EnumerateArray())
                    found.Add(HueClient.LocalAddress(bridge.GetProperty("internalipaddress").GetString()!));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException or System.Text.Json.JsonException) { }
        }
        return found.Order().ToArray();
    }
    public static string[] ParseAddresses(byte[] packet)
    {
        var addresses = new List<string>();
        if (packet.Length < 12)
            return [];
        int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at, 2));
        int SkipName(int pos)
        {
            for (var i = 0; i < 128; i++)
            {
                if (pos >= packet.Length)
                    throw new FormatException();
                var length = packet[pos++];
                if (length == 0)
                    return pos;
                if ((length & 192) == 192)
                    return pos + 1;
                if (length > 63)
                    throw new FormatException();
                pos += length;
            }
            throw new FormatException();
        }
        try
        {
            var pos = 12;
            var questions = U16(4);
            var records = U16(6) + U16(8) + U16(10);
            if (questions > 100 || records > 1000)
                return [];
            for (var i = 0; i < questions; i++)
                pos = SkipName(pos) + 4;
            for (var i = 0; i < records; i++)
            {
                pos = SkipName(pos);
                var type = U16(pos);
                var length = U16(pos + 8);
                pos += 10;
                if (pos + length > packet.Length)
                    return [];
                if (type == 1 && length == 4)
                {
                    var ip = new IPAddress(packet.AsSpan(pos, 4)).ToString();
                    try
                    {
                        addresses.Add(HueClient.LocalAddress(ip));
                    }
                    catch (ArgumentException) { }
                }
                pos += length;
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException) { return []; }
        return addresses.Distinct().ToArray();
    }
}
