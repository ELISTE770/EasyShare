using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyShare.Models;
using EasyShare.Services;

namespace EasyShare.Core;

/// <summary>
/// שירות גילוי עמיתים אוטומטי (Zero-Config UDP Beacon & Discovery),
/// ניהול מזהה התקנה קבוע (Peer ID), שיתוף ודרופ קבצים ישיר וצ'אט בין מחשבים.
/// </summary>
public sealed class PeerDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 2122;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly ConcurrentDictionary<string, PeerDevice> _peers = new(StringComparer.OrdinalIgnoreCase);

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _beaconTask;

    private int? _overridePort;
    public string LocalPeerId => _settings.PeerId;
    public string LocalDeviceName => _settings.DeviceName;
    public int LocalServerPort
    {
        get => _overridePort ?? _settings.ServerPort;
        set => _overridePort = value;
    }

    public InternetPeerDiscoveryService InternetDiscovery { get; }

    public event Action<PeerDevice>? OnPeerDiscovered;
    public event Action<PeerDevice>? OnPeerUpdated;
    public event Action<string>? OnPeerLost;
    public event Action<string, string, string, string>? OnDirectFileReceived;
    public event Action<string, string, string>? OnDirectMessageReceived;
    public event Action<string>? OnLog;
    public event Action<PeerChatMessage>? OnChatMessageAdded;

    private readonly ConcurrentDictionary<string, List<PeerChatMessage>> _chatHistories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// מחזיר את כל היסטוריית ההודעות עם עמית מסוים
    /// </summary>
    public IReadOnlyList<PeerChatMessage> GetChatHistory(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId)) return Array.Empty<PeerChatMessage>();
        if (_chatHistories.TryGetValue(peerId.Trim(), out var list))
        {
            lock (list)
            {
                return list.ToList();
            }
        }
        return Array.Empty<PeerChatMessage>();
    }

    /// <summary>
    /// מנקה את היסטוריית ההודעות עבור עמית מסוים
    /// </summary>
    public void ClearChatHistory(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId)) return;
        if (_chatHistories.TryGetValue(peerId.Trim(), out var list))
        {
            lock (list)
            {
                list.Clear();
            }
        }
    }

    /// <summary>
    /// מוסיף הודעת צ'אט (נכנסת או יוצאת) להיסטוריה ומפעיל אירוע עדכון חי
    /// </summary>
    public void AddChatMessage(PeerChatMessage msg)
    {
        string conversationKey = msg.IsOutgoing ? msg.RecipientPeerId : msg.SenderPeerId;
        if (string.IsNullOrWhiteSpace(conversationKey)) return;
        conversationKey = conversationKey.Trim();

        var list = _chatHistories.GetOrAdd(conversationKey, _ => new List<PeerChatMessage>());
        lock (list)
        {
            list.Add(msg);
        }
        OnChatMessageAdded?.Invoke(msg);
    }

    public PeerDiscoveryService(SettingsService? settingsService = null, int? localPort = null)
    {
        _settingsService = settingsService ?? new SettingsService();
        _settings = _settingsService.Current;
        _overridePort = localPort;

        InternetDiscovery = new InternetPeerDiscoveryService(_settingsService);
        InternetDiscovery.OnInternetPeerDiscovered += peer =>
        {
            _peers[peer.PeerId] = peer;
            OnPeerDiscovered?.Invoke(peer);
        };
        InternetDiscovery.OnRelayMessageReceived += (senderId, senderName, text) =>
        {
            NotifyDirectMessageReceived(senderId, senderName, text);
        };
        InternetDiscovery.OnLog += msg => OnLog?.Invoke(msg);

        EnsurePeerId();
    }

    /// <summary>
    /// מוודא שקיים מזהה התקנה ייחודי בפורמט SB-XXX-XXX ושם מכשיר
    /// </summary>
    public void EnsurePeerId()
    {
        bool changed = false;
        if (string.IsNullOrWhiteSpace(_settings.PeerId) || !_settings.PeerId.StartsWith("SB-", StringComparison.OrdinalIgnoreCase))
        {
            var rnd = new Random();
            int p1 = rnd.Next(100, 1000);
            int p2 = rnd.Next(100, 1000);
            _settings.PeerId = $"SB-{p1}-{p2}";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_settings.DeviceName))
        {
            _settings.DeviceName = Environment.MachineName;
            changed = true;
        }

        if (changed)
        {
            _settingsService.Save(_settings);
        }
    }

    /// <summary>
    /// מתחיל את שירות הגילוי והשידור ברשת המקומית ובאינטרנט
    /// </summary>
    public void Start()
    {
        if (_settings.EnableInternetDiscovery)
        {
            InternetDiscovery.Start();
        }

        if (_udpClient != null) return;
        if (!_settings.EnablePeerDiscovery) return;

        _cts = new CancellationTokenSource();

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _udpClient.EnableBroadcast = true;

            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));
            _beaconTask = Task.Run(() => BeaconLoopAsync(_cts.Token));

            OnLog?.Invoke($"[PEER DISCOVERY] שירות גילוי עמיתים פעיל במזהה {LocalPeerId} (פורט {DiscoveryPort})");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[PEER DISCOVERY ERROR] שגיאה בהפעלת שירות גילוי: {ex.Message}");
        }
    }

    /// <summary>
    /// לולאת האזנה לפקטות גילוי UDP ברשת המקומית
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient!.ReceiveAsync(ct);
                string json = Encoding.UTF8.GetString(result.Buffer);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElem)) continue;
                string type = typeElem.GetString() ?? "";

                if (type == "beacon")
                {
                    string peerId = root.GetProperty("peerId").GetString() ?? "";
                    if (string.Equals(peerId, LocalPeerId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // שידור עצמי
                    }

                    string devName = root.TryGetProperty("deviceName", out var dElem) ? dElem.GetString() ?? "Unknown" : "Unknown";
                    int port = root.TryGetProperty("port", out var pElem) ? pElem.GetInt32() : 2121;
                    string os = root.TryGetProperty("os", out var oElem) ? oElem.GetString() ?? "" : "";
                    string senderIp = result.RemoteEndPoint.Address.ToString();

                    bool isNew = !_peers.ContainsKey(peerId);
                    var peer = _peers.GetOrAdd(peerId, id => new PeerDevice
                    {
                        PeerId = id,
                        DeviceName = devName,
                        IpAddress = senderIp,
                        Port = port,
                        OsDescription = os,
                        LastSeen = DateTime.UtcNow,
                        IsOnline = true
                    });

                    peer.DeviceName = devName;
                    peer.IpAddress = senderIp;
                    peer.Port = port;
                    peer.OsDescription = os;
                    peer.LastSeen = DateTime.UtcNow;
                    peer.IsOnline = true;

                    if (isNew)
                    {
                        OnLog?.Invoke($"[PEER DISCOVERED] זוהה עמית חדש: {devName} ({peerId}) בכתובת {senderIp}:{port}");
                        OnPeerDiscovered?.Invoke(peer);
                    }
                    else
                    {
                        OnPeerUpdated?.Invoke(peer);
                    }
                }
                else if (type == "query")
                {
                    string target = root.TryGetProperty("targetPeerId", out var tElem) ? tElem.GetString() ?? "" : "";
                    if (string.Equals(target, LocalPeerId, StringComparison.OrdinalIgnoreCase))
                    {
                        // העמית מחפש אותנו - נשיב לו ישירות
                        await SendBeaconAsync(result.RemoteEndPoint);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(100, ct); }
        }
    }

    /// <summary>
    /// לולאת שידור פעימות נוכחות תקופתיות (Heartbeat Beacon)
    /// </summary>
    private async Task BeaconLoopAsync(CancellationToken ct)
    {
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendBeaconAsync(broadcastEndpoint);
                PruneStalePeers();
                await Task.Delay(4000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(1000, ct); }
        }
    }

    private async Task SendBeaconAsync(IPEndPoint target)
    {
        if (_udpClient == null) return;

        var beaconData = new
        {
            type = "beacon",
            peerId = LocalPeerId,
            deviceName = LocalDeviceName,
            port = LocalServerPort,
            os = "Windows"
        };

        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(beaconData));
        await _udpClient.SendAsync(bytes, bytes.Length, target);
    }

    /// <summary>
    /// משדר בקשת איתור מיידית עבור מזהה ספציפי ברשת המקומית וברשת האינטרנט (תומך גם במצב מוסתר)
    /// </summary>
    public async Task<PeerDevice?> QueryPeerAsync(string targetPeerId)
    {
        if (string.IsNullOrWhiteSpace(targetPeerId)) return null;
        string clean = targetPeerId.Trim();

        var existing = FindPeer(clean);
        if (existing != null) return existing;

        if (_udpClient != null)
        {
            var query = new
            {
                type = "query",
                targetPeerId = clean,
                senderPeerId = LocalPeerId
            };

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(query));
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
            await _udpClient.SendAsync(bytes, bytes.Length, broadcastEndpoint);
        }

        if (_settings.EnableInternetDiscovery)
        {
            var internetPeer = await InternetDiscovery.LookupPeerByIdAsync(clean);
            if (internetPeer != null)
            {
                _peers[internetPeer.PeerId] = internetPeer;
                OnPeerDiscovered?.Invoke(internetPeer);
                return internetPeer;
            }
        }

        var foundImmediate = FindPeer(clean);
        if (foundImmediate != null) return foundImmediate;

        // המתנה קצרה למענה שידור ה-UDP ברשת המקומית
        await Task.Delay(400);

        return FindPeer(clean);
    }

    /// <summary>
    /// מבצע חיפוש עמיתים גלויים ברשת האינטרנט
    /// </summary>
    public async Task<List<PeerDevice>> SearchInternetPeersAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (!_settings.EnableInternetDiscovery) return new List<PeerDevice>();

        var list = await InternetDiscovery.SearchVisiblePeersAsync(timeoutMs, ct);
        foreach (var peer in list)
        {
            _peers[peer.PeerId] = peer;
            OnPeerDiscovered?.Invoke(peer);
        }
        return list;
    }

    /// <summary>
    /// מנקה עמיתים שלא נצפו ברשת יותר מ-12 שניות
    /// </summary>
    private void PruneStalePeers()
    {
        var now = DateTime.UtcNow;
        var staleIds = _peers.Where(kvp => (now - kvp.Value.LastSeen).TotalSeconds > 12).Select(kvp => kvp.Key).ToList();

        foreach (var id in staleIds)
        {
            if (_peers.TryRemove(id, out var lostPeer))
            {
                lostPeer.IsOnline = false;
                OnLog?.Invoke($"[PEER LOST] הקשר עם {lostPeer.DeviceName} ({id}) נותק");
                OnPeerLost?.Invoke(id);
            }
        }
    }

    public IReadOnlyList<PeerDevice> GetDiscoveredPeers() => _peers.Values.Where(p => p.IsOnline).ToList();

    public PeerDevice? FindPeer(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId)) return null;
        string clean = peerId.Trim();
        if (_peers.TryGetValue(clean, out var p) && p.IsOnline) return p;

        return _peers.Values.FirstOrDefault(x => string.Equals(x.PeerId, clean, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// שליחת קבצים ישירה לעמית ברשת המקומית או באינטרנט (Direct Drop Files)
    /// </summary>
    public async Task<(bool Success, string Message)> DropFilesToPeerAsync(
        PeerDevice peer,
        IEnumerable<string> filePaths,
        CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            string baseUrl = peer.EffectiveUrl;
            string url = $"{baseUrl}/api/peer/drop";

            using var form = new MultipartFormDataContent();
            int fileCount = 0;

            foreach (var path in filePaths)
            {
                if (File.Exists(path))
                {
                    var fileStream = File.OpenRead(path);
                    var streamContent = new StreamContent(fileStream);
                    streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MimeTypes.GetMimeType(path));
                    form.Add(streamContent, "files", Path.GetFileName(path));
                    fileCount++;
                }
            }

            if (fileCount == 0)
            {
                return (false, "לא נבחרו קבצים תקינים להעברה.");
            }

            form.Headers.Add("X-Sender-PeerId", LocalPeerId);
            form.Headers.Add("X-Sender-DeviceName", Uri.EscapeDataString(LocalDeviceName));

            var res = await client.PostAsync(url, form, ct);
            if (res.IsSuccessStatusCode)
            {
                foreach (var path in filePaths)
                {
                    long size = 0;
                    try { size = new FileInfo(path).Length; } catch { }
                    AddChatMessage(new PeerChatMessage
                    {
                        SenderPeerId = LocalPeerId,
                        SenderName = LocalDeviceName,
                        RecipientPeerId = peer.PeerId,
                        Text = $"נשלח קובץ: {Path.GetFileName(path)}",
                        AttachedFileName = Path.GetFileName(path),
                        AttachedFilePath = path,
                        AttachedFileSize = size,
                        Timestamp = DateTime.Now,
                        IsOutgoing = true
                    });
                }

                OnLog?.Invoke($"[PEER DROP SUCCESS] נשלחו בהצלחה {fileCount} קבצים אל {peer.DisplayText}");
                return (true, $"נשלחו בהצלחה {fileCount} קבצים.");
            }

            return (false, $"המשתמש החזיר שגיאה: {res.StatusCode}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[PEER DROP ERROR] כשל בשליחת קבצים אל {peer.DisplayText}: {ex.Message}");
            return (false, $"שגיאת תקשורת: {ex.Message}");
        }
    }

    /// <summary>
    /// שליחת הודעת צ'אט ישירה לעמית
    /// </summary>
    public async Task<(bool Success, string Message)> SendDirectMessageAsync(
        PeerDevice peer,
        string message,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return (false, "הודעה ריקה.");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string baseUrl = peer.EffectiveUrl;
            string url = $"{baseUrl}/api/peer/message";

            var payload = new
            {
                senderPeerId = LocalPeerId,
                senderName = LocalDeviceName,
                text = message.Trim(),
                timestamp = DateTime.UtcNow
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var res = await client.PostAsync(url, content, ct);

            if (res.IsSuccessStatusCode)
            {
                AddChatMessage(new PeerChatMessage
                {
                    SenderPeerId = LocalPeerId,
                    SenderName = LocalDeviceName,
                    RecipientPeerId = peer.PeerId,
                    Text = message.Trim(),
                    Timestamp = DateTime.Now,
                    IsOutgoing = true
                });

                OnLog?.Invoke($"[PEER CHAT] נשלחה הודעה אל {peer.DisplayText}: {message}");
                return (true, "ההודעה נמסרה בהצלחה.");
            }

            // אם קריאת ה-HTTP נכשלה והעמית הוא עמית אינטרנט - ננסה דרך ערוץ ה-Relay של האינטרנט
            if (peer.DiscoverySource == "Internet")
            {
                bool relayed = await InternetDiscovery.SendRelayMessageAsync(peer.PeerId, message, ct);
                if (relayed)
                {
                    AddChatMessage(new PeerChatMessage
                    {
                        SenderPeerId = LocalPeerId,
                        SenderName = LocalDeviceName,
                        RecipientPeerId = peer.PeerId,
                        Text = message.Trim(),
                        Timestamp = DateTime.Now,
                        IsOutgoing = true
                    });

                    OnLog?.Invoke($"[PEER CHAT RELAY] הודעה הועברה בהצלחה דרך ערוץ האינטרנט אל {peer.DisplayText}");
                    return (true, "ההודעה נמסרה בהצלחה דרך ערוץ התקשורת באינטרנט.");
                }
            }

            return (false, $"סטטוס שגיאה מהמשתמש: {res.StatusCode}");
        }
        catch (Exception ex)
        {
            if (peer.DiscoverySource == "Internet")
            {
                bool relayed = await InternetDiscovery.SendRelayMessageAsync(peer.PeerId, message, ct);
                if (relayed)
                {
                    AddChatMessage(new PeerChatMessage
                    {
                        SenderPeerId = LocalPeerId,
                        SenderName = LocalDeviceName,
                        RecipientPeerId = peer.PeerId,
                        Text = message.Trim(),
                        Timestamp = DateTime.Now,
                        IsOutgoing = true
                    });

                    OnLog?.Invoke($"[PEER CHAT RELAY] הודעה הועברה בהצלחה דרך ערוץ האינטרנט אל {peer.DisplayText}");
                    return (true, "ההודעה נמסרה בהצלחה דרך ערוץ התקשורת באינטרנט.");
                }
            }
            return (false, $"שגיאת תקשורת: {ex.Message}");
        }
    }

    public void NotifyDirectFileReceived(string senderPeerId, string senderName, string fileName, string savedPath)
    {
        long size = 0;
        try { if (File.Exists(savedPath)) size = new FileInfo(savedPath).Length; } catch { }
        AddChatMessage(new PeerChatMessage
        {
            SenderPeerId = senderPeerId,
            SenderName = senderName,
            RecipientPeerId = LocalPeerId,
            Text = $"התקבל קובץ: {fileName}",
            AttachedFileName = fileName,
            AttachedFilePath = savedPath,
            AttachedFileSize = size,
            Timestamp = DateTime.Now,
            IsOutgoing = false
        });

        OnDirectFileReceived?.Invoke(senderPeerId, senderName, fileName, savedPath);
    }

    public void NotifyDirectMessageReceived(string senderPeerId, string senderName, string message)
    {
        AddChatMessage(new PeerChatMessage
        {
            SenderPeerId = senderPeerId,
            SenderName = senderName,
            RecipientPeerId = LocalPeerId,
            Text = message.Trim(),
            Timestamp = DateTime.Now,
            IsOutgoing = false
        });

        OnDirectMessageReceived?.Invoke(senderPeerId, senderName, message);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _udpClient = null;
        InternetDiscovery.Stop();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        InternetDiscovery.Dispose();
    }
}
