using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Core;

/// <summary>
/// שירות זיהוי מקומי mDNS (Multicast DNS / ZeroConf / Bonjour).
/// מאזין ל-224.0.0.251:5353 ומשיב לשאילתות עבור 'easyshare.local',
/// כך שכל מכשיר ברשת ה-Wi-Fi יוכל לגשת ישירות באמצעות http://easyshare.local:2121.
/// </summary>
public sealed class MdnsDiscoveryService : IDisposable
{
    private UdpClient? _udpListener;
    private CancellationTokenSource? _cts;
    private readonly int _serverPort;
    private readonly string _hostName;

    public MdnsDiscoveryService(int serverPort = 2121, string hostName = "easyshare.local")
    {
        _serverPort = serverPort;
        _hostName = hostName.ToLowerInvariant();
    }

    public void Start()
    {
        if (_udpListener != null) return;
        _cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                _udpListener = new UdpClient();
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                _udpListener.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"));

                byte[] hostBytes = Encoding.ASCII.GetBytes("easyshare");

                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var res = await _udpListener.ReceiveAsync(_cts.Token);
                        byte[] buffer = res.Buffer;

                        // בדיקה מהירה האם השאילתה מכילה את השם easyshare
                        if (ContainsSubsequence(buffer, hostBytes))
                        {
                            string localIp = NetworkHelper.GetLocalIpAddress();
                            if (IPAddress.TryParse(localIp, out var ipAddress))
                            {
                                byte[] response = BuildDnsResponse(buffer, ipAddress);
                                if (response.Length > 0)
                                {
                                    var multicastEndpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                                    await _udpListener.SendAsync(response, response.Length, multicastEndpoint);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { await Task.Delay(200); }
                }
            }
            catch { }
        });
    }

    private static bool ContainsSubsequence(byte[] source, byte[] pattern)
    {
        if (pattern.Length == 0) return true;
        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (char.ToLowerInvariant((char)source[i + j]) != char.ToLowerInvariant((char)pattern[j]))
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }

    private static byte[] BuildDnsResponse(byte[] query, IPAddress localIp)
    {
        try
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // DNS Header: ID מועתק מהשאילתה
            bw.Write(query[0]);
            bw.Write(query[1]);
            bw.Write((byte)0x84); // Flags: Response, Authoritative
            bw.Write((byte)0x00);
            bw.Write((byte)0x00); // QDCOUNT: 0
            bw.Write((byte)0x00);
            bw.Write((byte)0x00); // ANCOUNT: 1
            bw.Write((byte)0x01);
            bw.Write((byte)0x00); // NSCOUNT: 0
            bw.Write((byte)0x00);
            bw.Write((byte)0x00); // ARCOUNT: 0
            bw.Write((byte)0x00);

            // Answer Name: \x09easyshare\x05local\x00
            bw.Write((byte)9);
            bw.Write(Encoding.ASCII.GetBytes("easyshare"));
            bw.Write((byte)5);
            bw.Write(Encoding.ASCII.GetBytes("local"));
            bw.Write((byte)0);

            // Type A (1), Class IN (1)
            bw.Write((byte)0x00);
            bw.Write((byte)0x01);
            bw.Write((byte)0x80); // Cache-flush + IN
            bw.Write((byte)0x01);

            // TTL (120 seconds)
            bw.Write((byte)0x00);
            bw.Write((byte)0x00);
            bw.Write((byte)0x00);
            bw.Write((byte)0x78);

            // Data length (4 bytes for IPv4)
            bw.Write((byte)0x00);
            bw.Write((byte)0x04);

            // IPv4 bytes
            bw.Write(localIp.GetAddressBytes());

            return ms.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udpListener?.Dispose();
        _udpListener = null;
    }
}
