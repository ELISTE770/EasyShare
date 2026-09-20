using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyShare.Models;
using EasyShare.Services;

namespace EasyShare.Core;

/// <summary>
/// שירות גילוי ותקשורת עמיתים דרך האינטרנט (WAN Internet Discovery & Signaling).
/// תומך בשני מצבי נראות:
/// 1. "Visible" (מצב גלוי) - מופיע בחיפוש הציבורי באינטרנט.
/// 2. "Hidden" (מצב מוסתר) - אינו מופיע בחיפוש כללי, וניתן לאיתור ולהתחברות אך ורק בהזנת המזהה המדויק.
/// ממומש באופן עצמאי ב-Pure C# .NET 9 ללא תלות בספריות צד שלישי (MQTT 3.1.1 פתוח).
/// </summary>
public sealed class InternetPeerDiscoveryService : IDisposable
{
    private const string BaseTopic = "smartbinary/easyshare";
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ConcurrentDictionary<string, PeerDevice> _discoveredInternetPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PeerDevice>> _pendingLookups = new(StringComparer.OrdinalIgnoreCase);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _heartbeatTask;
    private bool _disposed;
    private ushort _packetIdCounter = 1;

    // תמיכה במצב בדיקות מקומיות מהירות (In-Memory / Test Relay)
    private static readonly ConcurrentDictionary<string, PeerDevice> _testRegistry = new(StringComparer.OrdinalIgnoreCase);
    public bool TestMode { get; set; } = false;

    public bool IsConnected => TestMode || (_tcpClient != null && _tcpClient.Connected && _stream != null);
    public string LocalPeerId => _settings.PeerId;
    public string LocalDeviceName => _settings.DeviceName;
    public string VisibilityMode => _settings.InternetVisibilityMode; // "Hidden" or "Visible"
    public string? CurrentInternetUrl { get; set; }

    public event Action<PeerDevice>? OnInternetPeerDiscovered;
    public event Action<string, string, string>? OnRelayMessageReceived;
    public event Action<string>? OnLog;

    public InternetPeerDiscoveryService(SettingsService? settingsService = null)
    {
        _settingsService = settingsService ?? new SettingsService();
        _settings = _settingsService.Current;
    }

    /// <summary>
    /// מתחיל את שירות הגילוי באינטרנט
    /// </summary>
    public void Start()
    {
        if (_disposed || !_settings.EnableInternetDiscovery) return;
        if (TestMode)
        {
            RegisterLocalToTestRegistry();
            return;
        }

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectAndRunLoopAsync(_cts.Token));
    }

    /// <summary>
    /// מעדכן את מצב הנראות באינטרנט (גלוי או מוסתר)
    /// </summary>
    public async Task SetVisibilityModeAsync(string mode)
    {
        string normalized = string.Equals(mode, "Visible", StringComparison.OrdinalIgnoreCase) ? "Visible" : "Hidden";
        _settings.InternetVisibilityMode = normalized;
        _settingsService.Save(_settings);

        if (TestMode)
        {
            RegisterLocalToTestRegistry();
        }
        else if (IsConnected)
        {
            if (normalized == "Visible")
            {
                await PublishPresenceBeaconAsync();
            }
            OnLog?.Invoke($"[INTERNET PEER] מצב נראות באינטרנט עודכן ל: {(normalized == "Visible" ? "גלוי 🌐" : "מוסתר 🔒 (מזהה מדויק בלבד)")}");
        }
    }

    /// <summary>
    /// חיפוש עמיתים גלויים באינטרנט (מחזיר רק עמיתים שבחרו במצב גלוי)
    /// </summary>
    public async Task<List<PeerDevice>> SearchVisiblePeersAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (TestMode)
        {
            var list = new List<PeerDevice>();
            foreach (var p in _testRegistry.Values)
            {
                if (!string.Equals(p.PeerId, LocalPeerId, StringComparison.OrdinalIgnoreCase) && p.IsOnline)
                {
                    list.Add(p);
                }
            }
            return list;
        }

        string queryId = Guid.NewGuid().ToString("N")[..8];
        string replyTopic = $"{BaseTopic}/reply/{queryId}";

        try
        {
            if (!IsConnected)
            {
                await EnsureConnectedAsync(ct);
            }

            if (!IsConnected) return new List<PeerDevice>(_discoveredInternetPeers.Values);

            // הרשמה זמנית לערוץ התשובות
            await SubscribeAsync(replyTopic, ct);

            // שידור שאילתת גילוי בערוץ הציבורי
            var queryObj = new
            {
                type = "discover_visible",
                queryId = queryId,
                replyTopic = replyTopic,
                senderPeerId = LocalPeerId
            };

            await PublishJsonAsync($"{BaseTopic}/discover", queryObj, ct);
            await Task.Delay(timeoutMs, ct);
        }
        catch { }

        return new List<PeerDevice>(_discoveredInternetPeers.Values);
    }

    /// <summary>
    /// איתור עמית ספציפי באינטרנט לפי מזהה מדויק (עובד הן עבור עמית גלוי והן עבור עמית במצב מוסתר)
    /// </summary>
    public async Task<PeerDevice?> LookupPeerByIdAsync(string targetPeerId, int timeoutMs = 3500, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPeerId)) return null;
        string cleanId = targetPeerId.Trim();

        if (TestMode)
        {
            if (_testRegistry.TryGetValue(cleanId, out var testPeer) && testPeer.IsOnline)
            {
                return testPeer;
            }
            return null;
        }

        // בדיקה במטמון עמיתים פעילים
        if (_discoveredInternetPeers.TryGetValue(cleanId, out var cached) && 
            (DateTime.UtcNow - cached.LastSeen).TotalMinutes < 3)
        {
            return cached;
        }

        string queryId = Guid.NewGuid().ToString("N");
        string replyTopic = $"{BaseTopic}/reply/{queryId}";
        var tcs = new TaskCompletionSource<PeerDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingLookups[queryId] = tcs;

        try
        {
            if (!IsConnected)
            {
                await EnsureConnectedAsync(ct);
            }

            if (!IsConnected) return null;

            // הרשמה לערוץ התשובה
            await SubscribeAsync(replyTopic, ct);

            // שליחת בקשת איתור ישירה לערוץ ההאזנה של המזהה
            var lookupObj = new
            {
                type = "lookup_request",
                queryId = queryId,
                replyTopic = replyTopic,
                targetPeerId = cleanId,
                senderPeerId = LocalPeerId,
                senderDeviceName = LocalDeviceName
            };

            await PublishJsonAsync($"{BaseTopic}/lookup/{cleanId}", lookupObj, ct);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeoutMs);

            using (linkedCts.Token.Register(() => tcs.TrySetResult(null!)))
            {
                var result = await tcs.Task;
                if (result != null)
                {
                    _discoveredInternetPeers[result.PeerId] = result;
                    OnInternetPeerDiscovered?.Invoke(result);
                    return result;
                }
            }
        }
        catch { }
        finally
        {
            _pendingLookups.TryRemove(queryId, out _);
        }

        return null;
    }

    /// <summary>
    /// שליחת הודעת צ'אט ישירה דרך ערוץ התקשורת באינטרנט
    /// </summary>
    public async Task<bool> SendRelayMessageAsync(
        string targetPeerId, 
        string message, 
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetPeerId) || string.IsNullOrWhiteSpace(message)) return false;
        string cleanId = targetPeerId.Trim();

        var payload = new
        {
            type = "direct_msg",
            senderPeerId = LocalPeerId,
            senderDeviceName = LocalDeviceName,
            text = message,
            timestamp = DateTime.UtcNow
        };

        if (TestMode)
        {
            OnLog?.Invoke($"[INTERNET CHAT TEST] הודעה אל {cleanId}: {message}");
            return true;
        }

        try
        {
            if (!IsConnected)
            {
                await EnsureConnectedAsync(ct);
            }

            if (!IsConnected) return false;

            await PublishJsonAsync($"{BaseTopic}/peer/{cleanId}/msg", payload, ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void RegisterPeerForTesting(PeerDevice peer)
    {
        _testRegistry[peer.PeerId] = peer;
    }

    private void RegisterLocalToTestRegistry()
    {
        _testRegistry[LocalPeerId] = new PeerDevice
        {
            PeerId = LocalPeerId,
            DeviceName = LocalDeviceName,
            DiscoverySource = "Internet",
            InternetUrl = CurrentInternetUrl ?? "http://127.0.0.1:2121",
            IpAddress = "127.0.0.1",
            Port = 2121,
            IsOnline = true,
            LastSeen = DateTime.UtcNow
        };
    }

    private async Task ConnectAndRunLoopAsync(CancellationToken ct)
    {
        string brokerHost = _settings.InternetRelayBroker;
        if (string.IsNullOrWhiteSpace(brokerHost)) brokerHost = "broker.hivemq.com";
        int brokerPort = 1883;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(brokerHost, brokerPort, ct);
                _stream = _tcpClient.GetStream();

                // שליחת פקטת CONNECT
                string clientId = $"SB_{LocalPeerId.Replace("-", "")}_{Guid.NewGuid().ToString("N")[..4]}";
                await SendMqttConnectAsync(_stream, clientId, ct);

                // קריאת CONNACK
                byte[] connack = new byte[4];
                int read = await _stream.ReadAsync(connack.AsMemory(0, 4), ct);
                if (read < 4 || connack[0] != 0x20 || connack[3] != 0x00)
                {
                    throw new IOException("MQTT Connect Rejected");
                }

                OnLog?.Invoke($"[INTERNET DISCOVERY] מחובר לשרת התקשורת באינטרנט ({brokerHost}) במזהה {LocalPeerId}");

                // הרשמה לערוצים הרלוונטיים למכשיר
                // 1. ערוץ איתור פרטי לפי מזהה מדויק
                await SubscribeAsync($"{BaseTopic}/lookup/{LocalPeerId}", ct);
                // 2. ערוץ הודעות פרטיות
                await SubscribeAsync($"{BaseTopic}/peer/{LocalPeerId}/msg", ct);
                // 3. ערוץ גילוי ציבורי (רק אם במצב גלוי)
                await SubscribeAsync($"{BaseTopic}/discover", ct);
                await SubscribeAsync($"{BaseTopic}/public_peers", ct);

                // פרסום נוכחות ראשוני במידה והמצב גלוי
                if (VisibilityMode == "Visible")
                {
                    await PublishPresenceBeaconAsync(ct);
                }

                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(ct), ct);
                await ReadLoopAsync(_stream, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[INTERNET DISCOVERY] חיבור אינטרנט בהמתנה ({ex.Message}). ניסיון חידוש בעוד 15 שניות...");
                CloseConnection();
                await Task.Delay(15000, ct);
            }
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        try
        {
            string brokerHost = !string.IsNullOrWhiteSpace(_settings.InternetRelayBroker) 
                ? _settings.InternetRelayBroker 
                : "broker.hivemq.com";
            
            _tcpClient = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            await _tcpClient.ConnectAsync(brokerHost, 1883, linked.Token);
            _stream = _tcpClient.GetStream();

            string clientId = $"SB_{LocalPeerId.Replace("-", "")}_{Guid.NewGuid().ToString("N")[..4]}";
            await SendMqttConnectAsync(_stream, clientId, linked.Token);

            byte[] connack = new byte[4];
            int read = await _stream.ReadAsync(connack.AsMemory(0, 4), linked.Token);
            if (read >= 4 && connack[0] == 0x20 && connack[3] == 0x00)
            {
                await SubscribeAsync($"{BaseTopic}/lookup/{LocalPeerId}", linked.Token);
                await SubscribeAsync($"{BaseTopic}/peer/{LocalPeerId}/msg", linked.Token);
                _readTask = Task.Run(() => ReadLoopAsync(_stream, _cts?.Token ?? CancellationToken.None));
            }
        }
        catch { }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] header = new byte[1];
            int r = await stream.ReadAsync(header.AsMemory(0, 1), ct);
            if (r == 0) break;

            byte packetType = (byte)(header[0] >> 4);
            int remainingLength = await ReadRemainingLengthAsync(stream, ct);
            if (remainingLength < 0) break;

            byte[] body = new byte[remainingLength];
            int totalRead = 0;
            while (totalRead < remainingLength)
            {
                int chunk = await stream.ReadAsync(body.AsMemory(totalRead, remainingLength - totalRead), ct);
                if (chunk == 0) break;
                totalRead += chunk;
            }

            if (packetType == 3) // PUBLISH
            {
                HandleIncomingPublish(body);
            }
        }
    }

    private void HandleIncomingPublish(byte[] body)
    {
        try
        {
            if (body.Length < 2) return;
            int topicLen = (body[0] << 8) | body[1];
            if (body.Length < 2 + topicLen) return;

            string topic = Encoding.UTF8.GetString(body, 2, topicLen);
            string payload = Encoding.UTF8.GetString(body, 2 + topicLen, body.Length - (2 + topicLen));

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var tElem) ? tElem.GetString() ?? "" : "";

            if (type == "lookup_request")
            {
                // בקשת איתור עבורנו! גם אם אנחנו במצב מוסתר, נשיב ישירות לערוץ התשובה
                string replyTopic = root.GetProperty("replyTopic").GetString() ?? "";
                string queryId = root.GetProperty("queryId").GetString() ?? "";
                if (!string.IsNullOrEmpty(replyTopic))
                {
                    _ = Task.Run(async () =>
                    {
                        var response = new
                        {
                            type = "lookup_response",
                            queryId = queryId,
                            peerId = LocalPeerId,
                            deviceName = LocalDeviceName,
                            internetUrl = CurrentInternetUrl ?? "",
                            port = _settings.ServerPort,
                            visibility = VisibilityMode,
                            timestamp = DateTime.UtcNow
                        };
                        await PublishJsonAsync(replyTopic, response);
                    });
                }
            }
            else if (type == "lookup_response")
            {
                string queryId = root.TryGetProperty("queryId", out var qElem) ? qElem.GetString() ?? "" : "";
                if (_pendingLookups.TryGetValue(queryId, out var tcs))
                {
                    string peerId = root.GetProperty("peerId").GetString() ?? "";
                    string devName = root.TryGetProperty("deviceName", out var dElem) ? dElem.GetString() ?? "" : "";
                    string iUrl = root.TryGetProperty("internetUrl", out var uElem) ? uElem.GetString() ?? "" : "";
                    int port = root.TryGetProperty("port", out var pElem) ? pElem.GetInt32() : 2121;

                    var peer = new PeerDevice
                    {
                        PeerId = peerId,
                        DeviceName = devName,
                        DiscoverySource = "Internet",
                        InternetUrl = iUrl,
                        Port = port,
                        IsOnline = true,
                        LastSeen = DateTime.UtcNow
                    };
                    tcs.TrySetResult(peer);
                }
            }
            else if (type == "discover_visible" && VisibilityMode == "Visible")
            {
                // בקשת גילוי כללית - משיבים רק אם אנחנו במצב גלוי
                string replyTopic = root.GetProperty("replyTopic").GetString() ?? "";
                if (!string.IsNullOrEmpty(replyTopic))
                {
                    _ = Task.Run(async () =>
                    {
                        var response = new
                        {
                            type = "public_presence",
                            peerId = LocalPeerId,
                            deviceName = LocalDeviceName,
                            internetUrl = CurrentInternetUrl ?? "",
                            port = _settings.ServerPort,
                            timestamp = DateTime.UtcNow
                        };
                        await PublishJsonAsync(replyTopic, response);
                    });
                }
            }
            else if (type == "public_presence")
            {
                string peerId = root.GetProperty("peerId").GetString() ?? "";
                if (string.Equals(peerId, LocalPeerId, StringComparison.OrdinalIgnoreCase)) return;

                string devName = root.TryGetProperty("deviceName", out var dElem) ? dElem.GetString() ?? "" : "";
                string iUrl = root.TryGetProperty("internetUrl", out var uElem) ? uElem.GetString() ?? "" : "";
                int port = root.TryGetProperty("port", out var pElem) ? pElem.GetInt32() : 2121;

                var peer = _discoveredInternetPeers.GetOrAdd(peerId, id => new PeerDevice
                {
                    PeerId = id,
                    DeviceName = devName,
                    DiscoverySource = "Internet",
                    InternetUrl = iUrl,
                    Port = port,
                    IsOnline = true,
                    LastSeen = DateTime.UtcNow
                });

                peer.DeviceName = devName;
                peer.InternetUrl = iUrl;
                peer.Port = port;
                peer.LastSeen = DateTime.UtcNow;
                peer.IsOnline = true;

                OnInternetPeerDiscovered?.Invoke(peer);
            }
            else if (type == "direct_msg")
            {
                string senderPeerId = root.GetProperty("senderPeerId").GetString() ?? "";
                string senderDeviceName = root.TryGetProperty("senderDeviceName", out var snElem) ? snElem.GetString() ?? "" : "";
                string text = root.GetProperty("text").GetString() ?? "";

                OnRelayMessageReceived?.Invoke(senderPeerId, senderDeviceName, text);
            }
        }
        catch { }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (VisibilityMode == "Visible")
                {
                    await PublishPresenceBeaconAsync(ct);
                }
                await Task.Delay(10000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(5000, ct); }
        }
    }

    private async Task PublishPresenceBeaconAsync(CancellationToken ct = default)
    {
        var beacon = new
        {
            type = "public_presence",
            peerId = LocalPeerId,
            deviceName = LocalDeviceName,
            internetUrl = CurrentInternetUrl ?? "",
            port = _settings.ServerPort,
            timestamp = DateTime.UtcNow
        };

        await PublishJsonAsync($"{BaseTopic}/public_peers", beacon, ct);
    }

    private async Task PublishJsonAsync(string topic, object payload, CancellationToken ct = default)
    {
        if (_stream == null) return;
        string json = JsonSerializer.Serialize(payload);
        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        byte[] payloadBytes = Encoding.UTF8.GetBytes(json);

        int remainingLength = 2 + topicBytes.Length + payloadBytes.Length;
        byte[] remBytes = EncodeRemainingLength(remainingLength);

        using var ms = new MemoryStream();
        ms.WriteByte(0x30); // PUBLISH QoS 0
        ms.Write(remBytes);
        ms.WriteByte((byte)(topicBytes.Length >> 8));
        ms.WriteByte((byte)(topicBytes.Length & 0xFF));
        ms.Write(topicBytes);
        ms.Write(payloadBytes);

        byte[] packet = ms.ToArray();
        await _stream.WriteAsync(packet, ct);
        await _stream.FlushAsync(ct);
    }

    private async Task SubscribeAsync(string topic, CancellationToken ct = default)
    {
        if (_stream == null) return;
        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        ushort pid = unchecked(++_packetIdCounter);

        int remainingLength = 2 + 2 + topicBytes.Length + 1;
        byte[] remBytes = EncodeRemainingLength(remainingLength);

        using var ms = new MemoryStream();
        ms.WriteByte(0x82); // SUBSCRIBE
        ms.Write(remBytes);
        ms.WriteByte((byte)(pid >> 8));
        ms.WriteByte((byte)(pid & 0xFF));
        ms.WriteByte((byte)(topicBytes.Length >> 8));
        ms.WriteByte((byte)(topicBytes.Length & 0xFF));
        ms.Write(topicBytes);
        ms.WriteByte(0x00); // QoS 0

        byte[] packet = ms.ToArray();
        await _stream.WriteAsync(packet, ct);
        await _stream.FlushAsync(ct);
    }

    private static async Task SendMqttConnectAsync(NetworkStream stream, string clientId, CancellationToken ct)
    {
        byte[] clientBytes = Encoding.UTF8.GetBytes(clientId);
        byte[] protoBytes = Encoding.ASCII.GetBytes("MQTT");

        int remainingLength = 2 + protoBytes.Length + 1 + 1 + 2 + 2 + clientBytes.Length;
        byte[] remBytes = EncodeRemainingLength(remainingLength);

        using var ms = new MemoryStream();
        ms.WriteByte(0x10); // CONNECT
        ms.Write(remBytes);
        ms.WriteByte(0x00);
        ms.WriteByte(0x04);
        ms.Write(protoBytes);
        ms.WriteByte(0x04); // Version 3.1.1
        ms.WriteByte(0x02); // Clean session
        ms.WriteByte(0x00); // Keep alive MSB
        ms.WriteByte(0x3C); // Keep alive 60s
        ms.WriteByte((byte)(clientBytes.Length >> 8));
        ms.WriteByte((byte)(clientBytes.Length & 0xFF));
        ms.Write(clientBytes);

        byte[] packet = ms.ToArray();
        await stream.WriteAsync(packet, ct);
        await stream.FlushAsync(ct);
    }

    private static byte[] EncodeRemainingLength(int length)
    {
        using var ms = new MemoryStream();
        do
        {
            byte encodedByte = (byte)(length % 128);
            length /= 128;
            if (length > 0)
            {
                encodedByte |= 128;
            }
            ms.WriteByte(encodedByte);
        } while (length > 0);
        return ms.ToArray();
    }

    private static async Task<int> ReadRemainingLengthAsync(Stream stream, CancellationToken ct)
    {
        int multiplier = 1;
        int value = 0;
        byte[] b = new byte[1];
        do
        {
            int read = await stream.ReadAsync(b.AsMemory(0, 1), ct);
            if (read == 0) return -1;
            byte encodedByte = b[0];
            value += (encodedByte & 127) * multiplier;
            multiplier *= 128;
            if ((encodedByte & 128) == 0) break;
        } while (multiplier <= 128 * 128 * 128);
        return value;
    }

    private void CloseConnection()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcpClient?.Close(); _tcpClient?.Dispose(); } catch { }
        _stream = null;
        _tcpClient = null;
    }

    public void Stop()
    {
        _cts?.Cancel();
        CloseConnection();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}
