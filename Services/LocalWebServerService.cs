using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyShare.Core;
using EasyShare.Http;
using EasyShare.Models;
using EasyShare.Web;

namespace EasyShare.Services;

/// <summary>
/// מנוע שרת ה-Web המקומי המשודרג (LocalWebServerService).
/// תומך במצב גישה מלאה לכל כונני המחשב (Full Computer Access) או תיקייה ספציפית,
/// סל מחזור של Windows, עורך טקסט מובנה, הורדה ומחיקה מרובה (Batch), ופאנל הגדרות מתקדמות דינמי.
/// </summary>
public sealed class LocalWebServerService : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Core.MdnsDiscoveryService? _mdnsService;

    public record ClipboardItem(string Id, string Content, bool IsUrl, DateTime CreatedAt);
    private static readonly List<ClipboardItem> _clipboardItems = new();
    private static readonly ConcurrentDictionary<string, string> _webrtcSignals = new(StringComparer.OrdinalIgnoreCase);

    public record QuickChatMessage(string Id, string Sender, string Text, DateTime Timestamp);
    private static readonly List<QuickChatMessage> _quickMessages = new();
    public event Action<QuickChatMessage>? OnMessageReceived;

    private readonly ConcurrentDictionary<string, byte> _blacklistedIps = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? OnIpBlacklistedChanged;

    public SettingsService SettingsManager { get; }
    public AppSettings Settings => SettingsManager.Current;

    public int Port => Settings.ServerPort;
    public string RootDirectory => Settings.SharedFolderPath;
    public bool IsReadOnly { get => Settings.ServerReadOnly; set => Settings.ServerReadOnly = value; }
    public SecureTransferService SecurityService { get; }
    public CloudflareTunnelService TunnelService { get; }
    public Core.PeerDiscoveryService PeerDiscovery { get; }

    public string LocalIpAddress { get; private set; } = "127.0.0.1";
    public string LocalUrl => $"http://{LocalIpAddress}:{Port}";
    public bool IsRunning => _listener != null;

    public event Action<string>? OnLog;

    public bool IsIpBlacklisted(string ip) => _blacklistedIps.ContainsKey(ip.Trim());

    public void BlacklistIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        string clean = ip.Trim();
        _blacklistedIps[clean] = 1;
        if (!Settings.BlacklistedIps.Contains(clean, StringComparer.OrdinalIgnoreCase))
        {
            Settings.BlacklistedIps.Add(clean);
            SettingsManager.Save(Settings);
        }
        OnLog?.Invoke($"[SECURITY] IP {clean} נוסף לרשימת החסימות.");
        OnIpBlacklistedChanged?.Invoke(clean);
    }

    public void UnblacklistIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        string clean = ip.Trim();
        _blacklistedIps.TryRemove(clean, out _);
        Settings.BlacklistedIps.RemoveAll(x => string.Equals(x, clean, StringComparison.OrdinalIgnoreCase));
        SettingsManager.Save(Settings);
        OnLog?.Invoke($"[SECURITY] IP {clean} הוסר מרשימת החסימות.");
        OnIpBlacklistedChanged?.Invoke(clean);
    }

    public IReadOnlyCollection<string> GetBlacklistedIps() => _blacklistedIps.Keys.ToList();

    public IReadOnlyList<QuickChatMessage> GetRecentMessages()
    {
        lock (_quickMessages)
        {
            return _quickMessages.TakeLast(50).ToList();
        }
    }

    public void AddMessage(string sender, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var msg = new QuickChatMessage(Guid.NewGuid().ToString("N")[..8], sender.Trim(), text.Trim(), DateTime.Now);
        lock (_quickMessages)
        {
            _quickMessages.Add(msg);
            if (_quickMessages.Count > 100) _quickMessages.RemoveAt(0);
        }
        OnMessageReceived?.Invoke(msg);
        OnLog?.Invoke($"[CHAT] {msg.Sender}: {msg.Text}");
    }

    public void AddClipboardItem(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        bool isUrl = Uri.TryCreate(text, UriKind.Absolute, out var uriResult) &&
                     (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        var item = new ClipboardItem(Guid.NewGuid().ToString("N")[..8], text.Trim(), isUrl, DateTime.Now);
        lock (_clipboardItems)
        {
            _clipboardItems.Insert(0, item);
            if (_clipboardItems.Count > 50) _clipboardItems.RemoveAt(_clipboardItems.Count - 1);
        }
        OnLog?.Invoke($"[CLIPBOARD] Added shared item: {(text.Length > 30 ? text[..30] + "..." : text)}");
    }

    public ClipboardItem? GetLatestClipboardItem()
    {
        lock (_clipboardItems)
        {
            return _clipboardItems.FirstOrDefault();
        }
    }

    public LocalWebServerService(SettingsService? settingsService = null)
    {
        SettingsManager = settingsService ?? new SettingsService();
        SecurityService = new SecureTransferService(Settings.SecurityPin);
        TunnelService = new CloudflareTunnelService();
        PeerDiscovery = new Core.PeerDiscoveryService(SettingsManager, Port);

        // טעינת רשימת כתובות IP חסומות
        if (Settings.BlacklistedIps != null)
        {
            foreach (var bIp in Settings.BlacklistedIps)
            {
                if (!string.IsNullOrWhiteSpace(bIp)) _blacklistedIps[bIp.Trim()] = 1;
            }
        }

        SecurityService.OnIpBlocked += (ip, msg) => OnLog?.Invoke($"[SECURITY] {msg}");
        TunnelService.OnStateChanged += state => OnLog?.Invoke($"[TUNNEL] {state}");
        TunnelService.OnError += err => OnLog?.Invoke($"[TUNNEL ERROR] {err}");
        TunnelService.OnUrlChanged += url =>
        {
            PeerDiscovery.InternetDiscovery.CurrentInternetUrl = url;
            OnLog?.Invoke($"[PEER INTERNET URL] כתובת האינטרנט של העמית עודכנה אל: {url}");
        };
        PeerDiscovery.OnLog += msg => OnLog?.Invoke(msg);
    }

    public LocalWebServerService(
        string rootDirectory,
        int port = 2121,
        bool isReadOnly = false,
        string? pin = null,
        string? basicUser = null,
        string? basicPassword = null) : this(new SettingsService())
    {
        Settings.SharedFolderPath = Path.GetFullPath(rootDirectory);
        Settings.ServerPort = port;
        Settings.ServerReadOnly = isReadOnly;
        Settings.SecurityPin = pin;
        Settings.ServerAnonymous = string.IsNullOrEmpty(basicUser);
        Settings.ServerUsername = basicUser ?? "pc";
        Settings.ServerPassword = basicPassword ?? "";
        SecurityService.SetCustomPin(pin);
    }

    /// <summary>
    /// מתחיל את האזנת השרת המקומי
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        LocalIpAddress = NetworkHelper.GetPreferredLocalIpAddress();
        _cts = new CancellationTokenSource();

        int targetPort = Port > 0 ? Port : 2121;
        bool bound = false;

        for (int attempts = 0; attempts < 10; attempts++)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, targetPort);
                _listener.Start();
                bound = true;
                if (targetPort != Port)
                {
                    OnLog?.Invoke($"[SERVER] שים לב: יציאה {Port} תפוסה על ידי יישום אחר. השרת הופעל אוטומטית ביציאה פנויה {targetPort}.");
                    Settings.ServerPort = targetPort;
                    SettingsManager.Save(Settings);
                }
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _listener?.Stop();
                _listener = null;
                targetPort++;
            }
        }

        if (!bound)
        {
            throw new InvalidOperationException($"לא ניתן היה להפעיל את השרת ביציאה {Port} או ביציאות העוקבות אחריה (כולן תפוסות).");
        }

        OnLog?.Invoke($"[SERVER] Listening on {LocalUrl} (Mode: {Settings.AccessMode})");
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token));

        // הפעלת שירות mDNS לזיהוי מקומי מהיר (easyshare.local)
        try
        {
            _mdnsService = new Core.MdnsDiscoveryService(Port);
            _mdnsService.Start();
            OnLog?.Invoke($"[MDNS] Broadcasted local network name: http://easyshare.local:{Port}");
        }
        catch { }

        // נסיון פתיחת פורט אוטומטית בראוטר דרך UPnP ברקע (רק אם מופעל במפורש בהגדרות)
        if (Settings.EnableUpnp)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (Settings.ServerAnonymous && !SecurityService.IsPinRequired)
                    {
                        OnLog?.Invoke("[UPnP WARNING] UPnP is enabled while server has no PIN or password authentication! Router WAN mapping might expose unauthenticated access.");
                    }

                    bool upnpOk = await Core.UpnpPortForwarder.ForwardPortAsync(Port);
                    if (upnpOk && !string.IsNullOrEmpty(Core.UpnpPortForwarder.ExternalPublicIp))
                    {
                        OnLog?.Invoke($"[UPnP] Router port {Port} mapped successfully! Public WAN IP: {Core.UpnpPortForwarder.ExternalPublicIp}");
                    }
                }
                catch { }
            });
        }

        if (Settings.EnableCloudflareTunnel)
        {
            _ = Task.Run(async () =>
            {
                await TunnelService.StartAsync(
                    Port,
                    Settings.TunnelMode,
                    Settings.CloudflareTunnelToken,
                    Settings.CloudflareCustomDomain,
                    msg => OnLog?.Invoke($"[TUNNEL] {msg}"));
            });
        }

        // הפעלת שירות איתור עמיתים ברשת (Peer Discovery)
        if (Settings.EnablePeerDiscovery)
        {
            try
            {
                PeerDiscovery.Start();
                OnLog?.Invoke($"[PEER DISCOVERY] שירות גילוי עמיתים פעיל. מזהה: {PeerDiscovery.LocalPeerId} ({PeerDiscovery.LocalDeviceName})");
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[PEER DISCOVERY ERROR] שגיאה בהפעלת שירות גילוי עמיתים: {ex.Message}");
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    OnLog?.Invoke($"[SERVER ERROR] Accept error: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            string clientIp = "127.0.0.1";
            if (client.Client.RemoteEndPoint is IPEndPoint ipEp)
            {
                var addr = ipEp.Address;
                if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                clientIp = IPAddress.IsLoopback(addr) ? "127.0.0.1" : addr.ToString();
            }

            var request = await HttpRequest.ReadAsync(stream, clientIp, ct);
            if (request == null) return;

            // בדיקת כתובת IP חסומה ברשימה שחורה (Blacklist מנהלי)
            if (IsIpBlacklisted(clientIp))
            {
                await HttpResponse.WriteStatusAsync(stream, 403, "Forbidden", "Access denied: IP address is blacklisted by administrator.", ct);
                return;
            }

            // בדיקת כתובת IP חסומה עקב ניסיונות כושלים
            if (SecurityService.IsIpBlocked(clientIp, out string blockReason))
            {
                await HttpResponse.WriteStatusAsync(stream, 429, "Too Many Requests", blockReason, ct);
                return;
            }

            // אימות כותרת Host למניעת DNS Rebinding
            if (!IsValidHostHeader(request.GetHeader("Host")))
            {
                await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Invalid Host header.", ct);
                return;
            }

            // אימות Basic Auth
            if (!Settings.ServerAnonymous)
            {
                byte[] expectedPass = Encoding.UTF8.GetBytes(Settings.ServerPassword ?? "");
                byte[] actualPass = Encoding.UTF8.GetBytes(request.BasicAuthPassword ?? "");

                if (string.IsNullOrEmpty(Settings.ServerPassword) ||
                    request.BasicAuthUser != Settings.ServerUsername ||
                    !CryptographicOperations.FixedTimeEquals(actualPass, expectedPass))
                {
                    SecurityService.RecordFailedAttempt(clientIp);
                    await HttpResponse.WriteUnauthorizedBasicAsync(stream, "EasyShare", ct);
                    return;
                }
            }

            // אימות PIN ועוגיית סשן (Session Cookie)
            if (SecurityService.IsPinRequired && request.Path != "/api/auth/pin" && request.Path != "/" && !request.Path.StartsWith("/assets") && !request.Path.StartsWith("/secure") && !request.Path.StartsWith("/api/peer/"))
            {
                string? sessionToken = request.GetCookie("es_session");
                bool sessionValid = SecurityService.IsValidSession(sessionToken);

                if (!sessionValid)
                {
                    string? providedPin = request.GetHeader("X-PIN");
                    if (string.IsNullOrEmpty(providedPin) && request.Query.TryGetValue("pin", out var qPin))
                    {
                        providedPin = qPin;
                    }

                    if (string.IsNullOrEmpty(providedPin) || !SecurityService.ValidatePin(clientIp, providedPin))
                    {
                        await HttpResponse.WriteStatusAsync(stream, 401, "Unauthorized", "Invalid or missing PIN.", ct);
                        return;
                    }
                }
            }

            // טיפול בבקשות OPTIONS
            if (request.Method == "OPTIONS")
            {
                var optionsHeaders = new Dictionary<string, string>
                {
                    { "Allow", "GET, POST, HEAD, OPTIONS" }
                };
                await HttpResponse.WriteHeadersAsync(stream, 204, "No Content", "text/plain", 0, optionsHeaders, ct);
                return;
            }

            try
            {
                await RouteRequestAsync(request, stream, ct);
            }
            catch (UnauthorizedAccessException ex)
            {
                OnLog?.Invoke($"[FORBIDDEN] {request.Method} {request.Path}: {ex.Message}");
                await HttpResponse.WriteStatusAsync(stream, 403, "Forbidden", ex.Message, ct);
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[ROUTER ERROR] {request.Method} {request.Path}: {ex.Message}");
                await HttpResponse.WriteStatusAsync(stream, 500, "Internal Server Error", ex.Message, ct);
            }
        }
    }

    private bool IsValidHostHeader(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
        {
            return true;
        }

        string host = hostHeader.Trim();
        if (host.StartsWith('['))
        {
            int closeBracket = host.IndexOf(']');
            if (closeBracket > 0)
            {
                host = host[1..closeBracket];
            }
        }
        else
        {
            int colonIdx = host.IndexOf(':');
            if (colonIdx > 0)
            {
                host = host[..colonIdx];
            }
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::1", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(LocalIpAddress) && host.Equals(LocalIpAddress, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var parsedIp))
        {
            if (IPAddress.IsLoopback(parsedIp)) return true;
            byte[] bytes = parsedIp.GetAddressBytes();
            if (bytes.Length == 4)
            {
                if (bytes[0] == 10) return true; // 10.0.0.0/8
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12
                if (bytes[0] == 192 && bytes[1] == 168) return true; // 192.168.0.0/16
                if (bytes[0] == 169 && bytes[1] == 254) return true; // 169.254.0.0/16
            }
        }

        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Settings.EnableCloudflareTunnel)
        {
            if (host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(Settings.CloudflareCustomDomain) &&
                host.Equals(Settings.CloudflareCustomDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(TunnelService.CurrentUrl) &&
                Uri.TryCreate(TunnelService.CurrentUrl, UriKind.Absolute, out var tunnelUri) &&
                host.Equals(tunnelUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RouteRequestAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        // חסימת פעולות כתיבה במצב Read-Only - נבדק בראש הניתוב
        if (Settings.ServerReadOnly && req.Method != "GET" && req.Method != "HEAD" && req.Path != "/api/auth/pin" && req.Path != "/api/batch/download" && req.Path != "/api/webrtc/signal")
        {
            await HttpResponse.WriteStatusAsync(stream, 403, "Forbidden", "Server is operating in Read-Only mode.", ct);
            return;
        }

        // ממשק ה-Web המוטמע (SPA)
        if (req.Method == "GET" && (req.Path == "/" || req.Path == "/index.html"))
        {
            string html = EmbeddedResources.GetIndexHtml();
            await HttpResponse.WriteTextAsync(stream, html, "text/html; charset=utf-8", 200, "OK", ct);
            return;
        }

        // הגשת קובצי PWA Manifest ו-Service Worker
        if (req.Method == "GET" && (req.Path == "/manifest.json" || req.Path == "/manifest.webmanifest"))
        {
            string manifest = EmbeddedResources.GetManifestJson();
            await HttpResponse.WriteTextAsync(stream, manifest, "application/manifest+json; charset=utf-8", 200, "OK", ct);
            return;
        }

        if (req.Method == "GET" && req.Path == "/sw.js")
        {
            string sw = EmbeddedResources.GetServiceWorkerJs();
            var swHeaders = new Dictionary<string, string> { { "Service-Worker-Allowed", "/" } };
            byte[] swBytes = Encoding.UTF8.GetBytes(sw);
            await HttpResponse.WriteHeadersAsync(stream, 200, "OK", "application/javascript; charset=utf-8", swBytes.Length, swHeaders, ct);
            await stream.WriteAsync(swBytes, ct);
            await stream.FlushAsync(ct);
            return;
        }

        // הגשת צלמיות אתר ו-PWA
        if (req.Method == "GET" && (req.Path == "/favicon.ico" || req.Path == "/icon-192.png" || req.Path == "/icon-512.png"))
        {
            string filename = req.Path.TrimStart('/');
            byte[]? assetBytes = EmbeddedResources.GetAssetBytes(filename);
            if (assetBytes != null && assetBytes.Length > 0)
            {
                string contentType = filename.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) 
                    ? "image/x-icon" 
                    : "image/png";
                var cacheHeaders = new Dictionary<string, string> { { "Cache-Control", "public, max-age=86400" } };
                await HttpResponse.WriteHeadersAsync(stream, 200, "OK", contentType, assetBytes.Length, cacheHeaders, ct);
                await stream.WriteAsync(assetBytes, ct);
                await stream.FlushAsync(ct);
                return;
            }
        }

        // לוח שיתוף מהיר בזמן אמת (Quick Drop / Universal Clipboard Sync)
        if (req.Method == "GET" && req.Path == "/api/clipboard")
        {
            ClipboardItem? latest;
            List<ClipboardItem> list;
            lock (_clipboardItems)
            {
                list = _clipboardItems.OrderByDescending(x => x.CreatedAt).Take(30).ToList();
                latest = list.FirstOrDefault();
            }
            await HttpResponse.WriteJsonAsync(stream, new { text = latest?.Content ?? "", items = list }, 200, "OK", ct);
            return;
        }

        if (req.Method == "POST" && req.Path == "/api/clipboard")
        {
            string body = await req.ReadBodyAsStringAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                string? text = doc.RootElement.TryGetProperty("text", out var tElem) ? tElem.GetString() : null;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    bool isUrl = Uri.TryCreate(text, UriKind.Absolute, out var uriResult) &&
                                 (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

                    var item = new ClipboardItem(Guid.NewGuid().ToString("N")[..8], text.Trim(), isUrl, DateTime.Now);
                    lock (_clipboardItems)
                    {
                        _clipboardItems.Insert(0, item);
                        if (_clipboardItems.Count > 50) _clipboardItems.RemoveAt(_clipboardItems.Count - 1);
                    }
                    OnLog?.Invoke($"[CLIPBOARD] New shared item from {req.ClientIp}");
                    await HttpResponse.WriteJsonAsync(stream, new { success = true, item }, 200, "OK", ct);
                    return;
                }
            }
            catch { }
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing text", ct);
            return;
        }

        // איתות WebRTC P2P
        if (req.Method == "POST" && req.Path == "/api/webrtc/signal")
        {
            string body = await req.ReadBodyAsStringAsync(ct);
            try
            {
                using var doc = JsonDocument.Parse(body);
                string? roomId = doc.RootElement.TryGetProperty("roomId", out var rElem) ? rElem.GetString() : null;
                string? signal = doc.RootElement.TryGetProperty("signal", out var sElem) ? sElem.GetString() : null;

                if (!string.IsNullOrEmpty(roomId) && !string.IsNullOrEmpty(signal))
                {
                    _webrtcSignals[roomId] = signal;
                    await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
                    return;
                }
            }
            catch { }
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Invalid signal", ct);
            return;
        }

        if (req.Method == "GET" && req.Path == "/api/webrtc/signal")
        {
            if (req.Query.TryGetValue("roomId", out var roomId) && !string.IsNullOrEmpty(roomId))
            {
                if (_webrtcSignals.TryRemove(roomId, out var signal))
                {
                    await HttpResponse.WriteJsonAsync(stream, new { success = true, signal }, 200, "OK", ct);
                    return;
                }
            }
            await HttpResponse.WriteJsonAsync(stream, new { success = false }, 200, "OK", ct);
            return;
        }

        // נקודת סטטוס המערכת והאחסון
        if (req.Method == "GET" && req.Path == "/api/status")
        {
            var status = new
            {
                isReadOnly = Settings.ServerReadOnly,
                isPinRequired = SecurityService.IsPinRequired,
                port = Port,
                localIp = LocalIpAddress,
                localUrl = LocalUrl,
                tunnelUrl = TunnelService.CurrentUrl,
                isTunnelActive = TunnelService.IsRunning,
                accessMode = Settings.AccessMode,
                rootDirectory = Settings.SharedFolderPath,
                sendToRecycleBin = Settings.SendToRecycleBin,
                theme = Settings.ThemeMode,
                serverTheme = Core.ThemeService.Instance.CurrentTheme,
                blacklistedCount = _blacklistedIps.Count,
                peerId = PeerDiscovery.LocalPeerId,
                deviceName = PeerDiscovery.LocalDeviceName,
                peerDiscoveryEnabled = Settings.EnablePeerDiscovery,
                version = "2.5.0",
                language = Settings.Language
            };
            await HttpResponse.WriteJsonAsync(stream, status, 200, "OK", ct);
            return;
        }

        // סקירת כוננים ותיקיות מהירות במחשב
        if (req.Method == "GET" && req.Path == "/api/system/overview")
        {
            await HandleSystemOverviewAsync(stream, ct);
            return;
        }

        // קריאת הגדרות (DTO מסונן ומאובטח ללא חשיפת סודות)
        if (req.Method == "GET" && req.Path == "/api/settings")
        {
            var safeSettings = new
            {
                serverPort = Settings.ServerPort,
                serverReadOnly = Settings.ServerReadOnly,
                isPinRequired = SecurityService.IsPinRequired,
                hasPin = !string.IsNullOrEmpty(Settings.SecurityPin),
                sendToRecycleBin = Settings.SendToRecycleBin,
                showHiddenFiles = Settings.ShowHiddenFiles,
                foldersFirst = Settings.FoldersFirst,
                defaultViewMode = Settings.DefaultViewMode,
                themeMode = Settings.ThemeMode,
                language = Settings.Language,
                accessMode = Settings.AccessMode,
                sharedFolderPath = Settings.SharedFolderPath,
                serverAnonymous = Settings.ServerAnonymous,
                serverUsername = Settings.ServerUsername,
                peerId = PeerDiscovery.LocalPeerId,
                deviceName = PeerDiscovery.LocalDeviceName,
                enablePeerDiscovery = Settings.EnablePeerDiscovery,
                autoAcceptPeerDrops = Settings.AutoAcceptPeerDrops,
                enableCloudflareTunnel = Settings.EnableCloudflareTunnel,
                tunnelMode = Settings.TunnelMode,
                cloudflareCustomDomain = Settings.CloudflareCustomDomain
            };
            await HttpResponse.WriteJsonAsync(stream, safeSettings, 200, "OK", ct);
            return;
        }

        // שמירת הגדרות מתקדמות
        if (req.Method == "POST" && req.Path == "/api/settings")
        {
            await HandleSaveSettingsAsync(req, stream, ct);
            return;
        }

        // עמוד שיתוף מאובטח ייעודי
        if (req.Method == "GET" && (req.Path == "/secure" || req.Path == "/secure/"))
        {
            await HandleSecurePageAsync(req, stream, ct);
            return;
        }

        // הורדת קובץ משיתוף מאובטח
        if (req.Method == "GET" && req.Path == "/secure/download")
        {
            await HandleSecureDownloadAsync(req, stream, ct);
            return;
        }

        // סייר קבצים
        if (req.Method == "GET" && req.Path == "/api/browse")
        {
            await HandleBrowseAsync(req, stream, ct);
            return;
        }

        // הורדת קובץ או תיקייה
        if (req.Method == "GET" && req.Path == "/api/download")
        {
            await HandleDownloadAsync(req, stream, ct);
            return;
        }

        // צפייה בתוכן קובץ טקסט / קוד
        if (req.Method == "GET" && req.Path == "/api/file/content")
        {
            await HandleFileContentAsync(req, stream, ct);
            return;
        }

        // הפקת קוד QR
        if (req.Method == "GET" && req.Path == "/api/qrcode")
        {
            string targetUrl = TunnelService.CurrentUrl ?? LocalUrl;
            string svg = QrCodeGenerator.GenerateSvg(targetUrl);
            var qrResponse = new
            {
                url = LocalUrl,
                tunnelUrl = TunnelService.CurrentUrl,
                target = targetUrl,
                svg = svg
            };
            await HttpResponse.WriteJsonAsync(stream, qrResponse, 200, "OK", ct);
            return;
        }

        // אימות PIN
        if (req.Method == "POST" && req.Path == "/api/auth/pin")
        {
            await HandlePinAuthAsync(req, stream, ct);
            return;
        }

        // הורדה מרובה (Batch Download as ZIP)
        if (req.Method == "POST" && req.Path == "/api/batch/download")
        {
            await HandleBatchDownloadAsync(req, stream, ct);
            return;
        }


        // חיפוש רקורסיבי מהיר (Instant Recursive Search)
        if (req.Method == "GET" && req.Path == "/api/search")
        {
            await HandleSearchAsync(req, stream, ct);
            return;
        }

        // צ'אט ופתקים מהירים (Quick Text / Chat Drop)
        if (req.Method == "GET" && req.Path == "/api/messages")
        {
            await HandleGetMessagesAsync(stream, ct);
            return;
        }

        if (req.Method == "POST" && req.Path == "/api/messages")
        {
            await HandlePostMessageAsync(req, stream, ct);
            return;
        }

        // העלאה בחלקים (Chunked / Resumable Upload)
        if (req.Method == "POST" && req.Path == "/api/upload-chunk")
        {
            await HandleUploadChunkAsync(req, stream, ct);
            return;
        }

        // ניהול רשימה שחורה (Blacklist API)
        if (req.Method == "GET" && req.Path == "/api/blacklist")
        {
            await HttpResponse.WriteJsonAsync(stream, new { ips = GetBlacklistedIps() }, 200, "OK", ct);
            return;
        }

        if (req.Method == "POST" && req.Path == "/api/blacklist/add")
        {
            await HandleBlacklistAddAsync(req, stream, ct);
            return;
        }

        if (req.Method == "POST" && req.Path == "/api/blacklist/remove")
        {
            await HandleBlacklistRemoveAsync(req, stream, ct);
            return;
        }

        // העלאת קבצים
        if (req.Method == "POST" && req.Path == "/api/upload")
        {
            await HandleUploadAsync(req, stream, ct);
            return;
        }

        // יצירת תיקייה
        if (req.Method == "POST" && req.Path == "/api/mkdir")
        {
            await HandleMkdirAsync(req, stream, ct);
            return;
        }

        // מחיקת פריט יחיד
        if (req.Method == "POST" && req.Path == "/api/delete")
        {
            await HandleDeleteAsync(req, stream, ct);
            return;
        }

        // מחיקה מרובה (Batch Delete)
        if (req.Method == "POST" && req.Path == "/api/batch/delete")
        {
            await HandleBatchDeleteAsync(req, stream, ct);
            return;
        }

        // שינוי שם
        if (req.Method == "POST" && req.Path == "/api/rename")
        {
            await HandleRenameAsync(req, stream, ct);
            return;
        }

        // שמירת קובץ טקסט ערוך
        if (req.Method == "POST" && req.Path == "/api/file/save")
        {
            await HandleFileSaveAsync(req, stream, ct);
            return;
        }

        // מידע על מזהה העמית המקומי (Local Peer Info)
        if (req.Method == "GET" && req.Path == "/api/peer/my-info")
        {
            var myInfo = new
            {
                peerId = PeerDiscovery.LocalPeerId,
                deviceName = PeerDiscovery.LocalDeviceName,
                port = Port,
                localIp = LocalIpAddress,
                autoAccept = Settings.AutoAcceptPeerDrops,
                discoveryEnabled = Settings.EnablePeerDiscovery,
                internetDiscoveryEnabled = Settings.EnableInternetDiscovery,
                internetVisibility = PeerDiscovery.InternetDiscovery.VisibilityMode,
                internetUrl = PeerDiscovery.InternetDiscovery.CurrentInternetUrl ?? TunnelService.CurrentUrl
            };
            await HttpResponse.WriteJsonAsync(stream, myInfo, 200, "OK", ct);
            return;
        }

        // שינוי מצב נראות באינטרנט (גלוי / מוסתר)
        if (req.Method == "POST" && req.Path == "/api/peer/visibility")
        {
            string body = await req.ReadBodyAsStringAsync(ct);
            string mode = "Hidden";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("visibility", out var vElem))
                {
                    mode = vElem.GetString() ?? "Hidden";
                }
            }
            catch { }

            await PeerDiscovery.InternetDiscovery.SetVisibilityModeAsync(mode);
            await HttpResponse.WriteJsonAsync(stream, new { success = true, visibility = PeerDiscovery.InternetDiscovery.VisibilityMode }, 200, "OK", ct);
            return;
        }

        // חיפוש או איתור עמיתים באינטרנט (לפי מזהה מדויק או סריקה גלויה)
        if (req.Method == "POST" && req.Path == "/api/peer/search-internet")
        {
            string body = await req.ReadBodyAsStringAsync(ct);
            string targetPeerId = "";
            string queryType = "exact";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("targetPeerId", out var tElem)) targetPeerId = tElem.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("queryType", out var qElem)) queryType = qElem.GetString() ?? "exact";
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(targetPeerId))
            {
                var peer = await PeerDiscovery.InternetDiscovery.LookupPeerByIdAsync(targetPeerId);
                await HttpResponse.WriteJsonAsync(stream, new { found = peer != null, peer }, 200, "OK", ct);
                return;
            }
            else
            {
                var peers = await PeerDiscovery.SearchInternetPeersAsync();
                await HttpResponse.WriteJsonAsync(stream, new { count = peers.Count, peers }, 200, "OK", ct);
                return;
            }
        }

        // רשימת עמיתים שזוהו ברשת (Discovered Peers)
        if (req.Method == "GET" && req.Path == "/api/peer/discovered")
        {
            var peers = PeerDiscovery.GetDiscoveredPeers();
            await HttpResponse.WriteJsonAsync(stream, new { count = peers.Count, peers }, 200, "OK", ct);
            return;
        }

        // קבלת קבצים בדרופ ישיר מעמית ברשת (Direct Peer File Drop)
        if (req.Method == "POST" && req.Path == "/api/peer/drop")
        {
            await HandlePeerDropAsync(req, stream, ct);
            return;
        }

        // קבלת הודעת צ'אט ישירה מעמית ברשת (Direct Peer Message)
        if (req.Method == "POST" && req.Path == "/api/peer/message")
        {
            await HandlePeerDirectMessageAsync(req, stream, ct);
            return;
        }

        // שליחת קובץ או הודעה לעמית מרוחק מתוך הממשק (Direct Peer Send)
        if (req.Method == "POST" && req.Path == "/api/peer/send")
        {
            await HandlePeerSendAsync(req, stream, ct);
            return;
        }

        // שליפת היסטוריית צ'אט עם עמית
        if (req.Method == "GET" && req.Path == "/api/peer/chat")
        {
            await HandlePeerChatHistoryAsync(req, stream, ct);
            return;
        }

        // ניקוי היסטוריית צ'אט
        if (req.Method == "POST" && req.Path == "/api/peer/chat/clear")
        {
            await HandlePeerChatClearAsync(req, stream, ct);
            return;
        }

        // ניטור: שליפת רשימת כל הקישורים והקבצים המשותפים הפעילים
        if (req.Method == "GET" && req.Path == "/api/monitor/shares")
        {
            await HandleMonitorGetSharesAsync(req, stream, ct);
            return;
        }

        // ניטור: השמדת קישור שיתוף וטוקן גישה מיידית (ללא מחיקת הקובץ במחשב)
        if (req.Method == "POST" && req.Path == "/api/monitor/shares/revoke")
        {
            await HandleMonitorRevokeShareAsync(req, stream, ct);
            return;
        }

        // ניטור: השמדת כל קישורי השיתוף הפעילים בבת אחת (ללא מחיקת הקבצים במחשב)
        if (req.Method == "POST" && req.Path == "/api/monitor/shares/revoke-all")
        {
            await HandleMonitorRevokeAllSharesAsync(req, stream, ct);
            return;
        }

        await HttpResponse.WriteStatusAsync(stream, 404, "Not Found", "Endpoint not found.", ct);
    }

    private async Task HandleSystemOverviewAsync(Stream stream, CancellationToken ct)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d =>
            {
                long total = d.TotalSize;
                long free = d.AvailableFreeSpace;
                long used = total - free;
                double usedPercent = total > 0 ? Math.Round((double)used / total * 100, 1) : 0;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.Name == "C:\\" ? "כונן מקומי" : "כונן") : d.VolumeLabel;

                return new
                {
                    name = d.Name,
                    label = label,
                    driveType = d.DriveType.ToString(),
                    totalSize = total,
                    freeSpace = free,
                    usedPercent = usedPercent
                };
            }).ToList();

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var quickFolders = new List<object>
        {
            new { name = "הורדות", path = Path.Combine(userProfile, "Downloads"), icon = "downloads" },
            new { name = "שולחן עבודה", path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop), icon = "desktop" },
            new { name = "מסמכים", path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), icon = "documents" },
            new { name = "תמונות", path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), icon = "pictures" },
            new { name = "סרטונים", path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), icon = "videos" },
            new { name = "מוזיקה", path = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), icon = "music" }
        }.Where(q => Directory.Exists((string)((dynamic)q).path)).ToList();

        var overview = new
        {
            drives = drives,
            quickFolders = quickFolders,
            accessMode = Settings.AccessMode,
            currentRoot = Settings.SharedFolderPath
        };

        await HttpResponse.WriteJsonAsync(stream, overview, 200, "OK", ct);
    }

    private async Task HandleMonitorGetSharesAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        var transfers = SecureTransferService.GetAllActiveTransfers();
        var list = transfers.Select(t => new
        {
            token = t.Token,
            fileName = t.FileName,
            filePath = t.FilePath,
            fileSizeBytes = t.FileSizeBytes,
            formattedSize = t.FormattedSize,
            isFolder = t.IsFolder,
            channel = t.Channel,
            channelDisplay = t.ChannelDisplay,
            shareUrl = t.EffectiveUrl,
            pinCode = t.PinCode,
            securityDisplay = t.SecurityDisplay,
            createdAt = t.CreatedAt.ToString("o"),
            formattedCreatedAt = t.FormattedCreatedAt,
            expiresAt = t.ExpiresAt == DateTime.MaxValue ? null : t.ExpiresAt.ToString("o"),
            formattedExpiresAt = t.FormattedExpiresAt,
            maxDownloads = t.MaxDownloads,
            downloadCount = t.DownloadCount,
            isCancelled = t.IsCancelled,
            isExpired = t.IsExpired,
            statusDescription = t.StatusDescription
        }).ToList();

        await HttpResponse.WriteJsonAsync(stream, new { count = list.Count, shares = list }, 200, "OK", ct);
    }

    private async Task HandleMonitorRevokeShareAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string token = "";
        if (req.Query.TryGetValue("token", out var qToken))
        {
            token = qToken;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            string body = await req.ReadBodyAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("token", out var tProp))
                    {
                        token = tProp.GetString() ?? "";
                    }
                }
                catch { }
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            await HttpResponse.WriteJsonAsync(stream, new { success = false, error = "Token is required." }, 400, "Bad Request", ct);
            return;
        }

        bool revoked = SecureTransferService.RevokeTransfer(token, removeFromList: true);
        await HttpResponse.WriteJsonAsync(stream, new
        {
            success = revoked,
            token,
            message = revoked
                ? "קישור השיתוף הושמד בהצלחה. הקובץ המקורי במחשב נשמר בבטחה ללא שינוי."
                : "הקישור לא נמצא או שכבר הושמד."
        }, 200, "OK", ct);
    }

    private async Task HandleMonitorRevokeAllSharesAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        int count = SecureTransferService.RevokeAllTransfers(removeFromList: true);
        await HttpResponse.WriteJsonAsync(stream, new
        {
            success = true,
            revokedCount = count,
            message = "כל קישורי השיתוף הושמדו בהצלחה. הקבצים המקוריים במחשב נשמרו בבטחה ללא שינוי."
        }, 200, "OK", ct);
    }

    private async Task HandleSaveSettingsAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            bool changed = false;

            if (root.TryGetProperty("sendToRecycleBin", out var rb) && (rb.ValueKind == JsonValueKind.True || rb.ValueKind == JsonValueKind.False))
            {
                Settings.SendToRecycleBin = rb.GetBoolean();
                changed = true;
            }
            if (root.TryGetProperty("showHiddenFiles", out var sh) && (sh.ValueKind == JsonValueKind.True || sh.ValueKind == JsonValueKind.False))
            {
                Settings.ShowHiddenFiles = sh.GetBoolean();
                changed = true;
            }
            if (root.TryGetProperty("foldersFirst", out var ff) && (ff.ValueKind == JsonValueKind.True || ff.ValueKind == JsonValueKind.False))
            {
                Settings.FoldersFirst = ff.GetBoolean();
                changed = true;
            }
            if (root.TryGetProperty("defaultViewMode", out var vm) && vm.ValueKind == JsonValueKind.String)
            {
                Settings.DefaultViewMode = vm.GetString() ?? Settings.DefaultViewMode;
                changed = true;
            }
            if (root.TryGetProperty("themeMode", out var tm) && tm.ValueKind == JsonValueKind.String)
            {
                Settings.ThemeMode = tm.GetString() ?? Settings.ThemeMode;
                changed = true;
            }
            if (root.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String)
            {
                string? l = lang.GetString();
                if (!string.IsNullOrWhiteSpace(l))
                {
                    Settings.Language = l;
                    LocalizationService.Instance.SetLanguage(l);
                    changed = true;
                }
            }
            if (root.TryGetProperty("deviceName", out var dn) && dn.ValueKind == JsonValueKind.String)
            {
                string? dname = dn.GetString();
                if (!string.IsNullOrWhiteSpace(dname))
                {
                    Settings.DeviceName = dname.Trim();
                    changed = true;
                }
            }
            if (root.TryGetProperty("enablePeerDiscovery", out var epd) && (epd.ValueKind == JsonValueKind.True || epd.ValueKind == JsonValueKind.False))
            {
                Settings.EnablePeerDiscovery = epd.GetBoolean();
                changed = true;
            }
            if (root.TryGetProperty("autoAcceptPeerDrops", out var aapd) && (aapd.ValueKind == JsonValueKind.True || aapd.ValueKind == JsonValueKind.False))
            {
                Settings.AutoAcceptPeerDrops = aapd.GetBoolean();
                changed = true;
            }

            if (changed)
            {
                SettingsManager.Save(Settings);
                OnLog?.Invoke("[SETTINGS] Client preferences updated and saved successfully.");
            }

            await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
            return;
        }
        catch (Exception ex)
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", $"Invalid settings payload: {ex.Message}", ct);
        }
    }

    private async Task HandleBrowseAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string subPath = req.Query.TryGetValue("path", out var p) ? p : "";

        // אם מצב גישה הוא FullComputer ו-subPath ריק -> מציגים את תצוגת הכוננים והתיקיות המהירות
        if (string.IsNullOrWhiteSpace(subPath) && Settings.AccessMode == "FullComputer")
        {
            var rootOverview = new
            {
                isRoot = true,
                currentPath = "",
                items = new List<object>()
            };
            await HttpResponse.WriteJsonAsync(stream, rootOverview, 200, "OK", ct);
            return;
        }

        string targetDir = SafeResolvePath(subPath);

        if (!Directory.Exists(targetDir))
        {
            await HttpResponse.WriteStatusAsync(stream, 404, "Not Found", "Directory does not exist.", ct);
            return;
        }

        var dirInfo = new DirectoryInfo(targetDir);
        var items = new List<object>();

        // תיקיות
        foreach (var dir in dirInfo.EnumerateDirectories())
        {
            if (!Settings.ShowHiddenFiles && (dir.Attributes.HasFlag(FileAttributes.Hidden) || dir.Name.StartsWith('.')))
            {
                continue;
            }

            items.Add(new
            {
                name = dir.Name,
                path = dir.FullName,
                relativePath = GetDisplayPath(dir.FullName),
                isDirectory = true,
                size = 0L,
                modifiedDate = dir.LastWriteTimeUtc.ToString("o"),
                mimeType = "inode/directory"
            });
        }

        // קבצים
        foreach (var file in dirInfo.EnumerateFiles())
        {
            if (!Settings.ShowHiddenFiles && (file.Attributes.HasFlag(FileAttributes.Hidden) || file.Name.StartsWith('.')))
            {
                continue;
            }

            string mime = MimeTypes.GetMimeType(file.FullName);
            items.Add(new
            {
                name = file.Name,
                path = file.FullName,
                relativePath = GetDisplayPath(file.FullName),
                isDirectory = false,
                size = file.Length,
                modifiedDate = file.LastWriteTimeUtc.ToString("o"),
                mimeType = mime
            });
        }

        var responseData = new
        {
            isRoot = false,
            currentPath = targetDir,
            displayPath = GetDisplayPath(targetDir),
            parentPath = Directory.GetParent(targetDir)?.FullName,
            items = items
        };

        await HttpResponse.WriteJsonAsync(stream, responseData, 200, "OK", ct);
    }

    private async Task HandleDownloadAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        if (!req.Query.TryGetValue("path", out var subPath) || string.IsNullOrWhiteSpace(subPath))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing 'path' parameter.", ct);
            return;
        }

        string fullPath = SafeResolvePath(subPath);

        // אם מדובר בתיקייה - דחיסת ZIP ישירות ל-Stream
        if (Directory.Exists(fullPath))
        {
            string folderName = Path.GetFileName(fullPath.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(folderName)) folderName = "SharedFolder";
            string zipName = $"{folderName}.zip";

            await SecureTransferService.StreamDirectoryAsZipAsync(stream, fullPath, zipName, ct);
            return;
        }

        // אם מדובר בקובץ
        if (File.Exists(fullPath))
        {
            var fi = new FileInfo(fullPath);
            var conn = LiveNetworkMonitorService.Instance.RegisterConnection(req.ClientIp, fi.Name, fi.Length);
            try
            {
                string mime = MimeTypes.GetMimeType(fullPath);
                var fileHeaders = new Dictionary<string, string>();

                bool inlineRequested = req.Query.TryGetValue("inline", out var inl) && inl == "true";
                bool isSafeInlineMime = mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                                     || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                                     || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                                     || mime == "application/pdf"
                                     || mime.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);

                string encodedName = Uri.EscapeDataString(fi.Name);
                if (inlineRequested && isSafeInlineMime && !fi.Extension.Equals(".html", StringComparison.OrdinalIgnoreCase) && !fi.Extension.Equals(".htm", StringComparison.OrdinalIgnoreCase) && !fi.Extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    fileHeaders["Content-Disposition"] = $@"inline; filename=""{encodedName}""; filename*=UTF-8''{encodedName}";
                }
                else
                {
                    fileHeaders["Content-Disposition"] = $@"attachment; filename=""{encodedName}""; filename*=UTF-8''{encodedName}";
                }

                await HttpResponse.WriteFileAsync(stream, fullPath, mime, req.ByteRange, fileHeaders, ct);
            }
            finally
            {
                LiveNetworkMonitorService.Instance.UnregisterConnection(conn.ConnectionId);
            }
            return;
        }

        await HttpResponse.WriteStatusAsync(stream, 404, "Not Found", "File or directory not found.", ct);
    }

    private async Task HandleBatchDownloadAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        List<string>? paths = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("paths", out var pElem) && pElem.ValueKind == JsonValueKind.Array)
            {
                paths = pElem.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList()!;
            }
        }
        catch { }

        if (paths == null || paths.Count == 0)
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "No paths provided for batch download.", ct);
            return;
        }

        string zipName = $"Download_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
        var customHeaders = new Dictionary<string, string>
        {
            { "Content-Disposition", $@"attachment; filename=""{zipName}""" }
        };

        await HttpResponse.WriteHeadersAsync(stream, 200, "OK", "application/zip", null, customHeaders, ct);

        var failedFiles = new List<string>();

        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                byte[] copyBuffer = new byte[65536];

                foreach (var p in paths)
                {
                    if (ct.IsCancellationRequested) break;
                    string fullPath = SafeResolvePath(p);

                    if (File.Exists(fullPath))
                    {
                        try
                        {
                            string entryName = Path.GetFileName(fullPath);
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                            await using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                            await using var es = entry.Open();
                            int read;
                            while ((read = await fs.ReadAsync(copyBuffer, ct)) > 0)
                            {
                                await es.WriteAsync(copyBuffer.AsMemory(0, read), ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            failedFiles.Add($"{p}: {ex.Message}");
                        }
                    }
                    else if (Directory.Exists(fullPath))
                    {
                        string folderBase = Path.GetFileName(fullPath.TrimEnd('/', '\\'));
                        IEnumerable<string> files;
                        try
                        {
                            files = Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories);
                        }
                        catch (Exception ex)
                        {
                            failedFiles.Add($"{p}: {ex.Message}");
                            continue;
                        }

                        foreach (var f in files)
                        {
                            if (ct.IsCancellationRequested) break;
                            try
                            {
                                string rel = Path.Combine(folderBase, Path.GetRelativePath(fullPath, f)).Replace('\\', '/');
                                var entry = archive.CreateEntry(rel, CompressionLevel.Fastest);
                                await using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                                await using var es = entry.Open();
                                int read;
                                while ((read = await fs.ReadAsync(copyBuffer, ct)) > 0)
                                {
                                    await es.WriteAsync(copyBuffer.AsMemory(0, read), ct);
                                }
                            }
                            catch (Exception ex)
                            {
                                failedFiles.Add($"{f}: {ex.Message}");
                            }
                        }
                    }
                }

                if (failedFiles.Count > 0)
                {
                    try
                    {
                        var failEntry = archive.CreateEntry("_FAILED_FILES.txt", CompressionLevel.Fastest);
                        await using var fes = failEntry.Open();
                        byte[] failBytes = Encoding.UTF8.GetBytes("The following files could not be read (e.g. locked by another process):\r\n\r\n" + string.Join("\r\n", failedFiles));
                        await fes.WriteAsync(failBytes, ct);
                    }
                    catch { }
                }
            }

            await stream.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OnLog?.Invoke($"[ZIP STREAM ERROR] Batch download interrupted: {ex.Message}");
            // כותרות ה-HTTP כבר נשלחו (200 OK) - אין לכתוב תגובת שגיאת 500 לתוך נתוני ה-ZIP הבינאריים
        }
    }

    private async Task HandleFileContentAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        if (!req.Query.TryGetValue("path", out var p) || string.IsNullOrWhiteSpace(p))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing 'path'", ct);
            return;
        }

        string fullPath = SafeResolvePath(p);
        if (!File.Exists(fullPath))
        {
            await HttpResponse.WriteStatusAsync(stream, 404, "Not Found", "File not found.", ct);
            return;
        }

        var fi = new FileInfo(fullPath);
        if (fi.Length > 5 * 1024 * 1024) // הגבלה ל-5MB לעריכת טקסט
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "File too large for text editor (max 5MB).", ct);
            return;
        }

        string text = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct);
        var res = new
        {
            name = fi.Name,
            path = fullPath,
            size = fi.Length,
            content = text
        };
        await HttpResponse.WriteJsonAsync(stream, res, 200, "OK", ct);
    }

    private async Task HandleFileSaveAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string? path = null;
        string? content = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("path", out var pElem)) path = pElem.GetString();
            if (doc.RootElement.TryGetProperty("content", out var cElem)) content = cElem.GetString();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(path) || content == null)
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing path or content.", ct);
            return;
        }

        string fullPath = SafeResolvePath(path);
        await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct);
        OnLog?.Invoke($"[SAVE] File edited and saved: {fullPath}");

        await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
    }

    private async Task HandleUploadAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string subPath = req.Query.TryGetValue("path", out var p) ? p : "";
        string targetDir = SafeResolvePath(subPath);

        var savedFiles = await MultipartParser.ParseAndSaveFilesAsync(req, targetDir, ct);
        OnLog?.Invoke($"[UPLOAD] {savedFiles.Count} file(s) uploaded to '{targetDir}' from {req.ClientIp}");

        await HttpResponse.WriteJsonAsync(stream, new { success = true, count = savedFiles.Count, files = savedFiles }, 200, "OK", ct);
    }

    private async Task HandlePinAuthAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string? pin = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("pin", out var pElem)) pin = pElem.GetString();
        }
        catch { }

        bool valid = SecurityService.ValidatePin(req.ClientIp, pin);
        if (valid)
        {
            string sessionToken = SecurityService.CreateSession();
            var authHeaders = new Dictionary<string, string>
            {
                { "Set-Cookie", $"es_session={sessionToken}; Path=/; HttpOnly; SameSite=Strict; Max-Age=86400" }
            };
            await HttpResponse.WriteJsonAsync(stream, new { success = true, token = sessionToken }, 200, "OK", authHeaders, ct);
        }
        else
        {
            await HttpResponse.WriteStatusAsync(stream, 401, "Unauthorized", "Invalid PIN.", ct);
        }
    }

    private async Task HandleMkdirAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string? parentPath = "";
        string? name = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("parentPath", out var pElem)) parentPath = pElem.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("name", out var nElem)) name = nElem.GetString();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(name))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Directory name is required.", ct);
            return;
        }

        name = Path.GetFileName(name);
        string parentDir = SafeResolvePath(parentPath);
        string newDir = Path.Combine(parentDir, name);

        Directory.CreateDirectory(newDir);
        OnLog?.Invoke($"[MKDIR] Created directory: {newDir}");

        await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
    }

    private async Task HandleDeleteAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string? path = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("path", out var pElem)) path = pElem.GetString();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(path))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Path is required.", ct);
            return;
        }

        string fullPath = SafeResolvePath(path);
        DeleteItemInternal(fullPath);

        await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
    }

    private async Task HandleBatchDeleteAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        List<string>? paths = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("paths", out var pElem) && pElem.ValueKind == JsonValueKind.Array)
            {
                paths = pElem.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList()!;
            }
        }
        catch { }

        if (paths == null || paths.Count == 0)
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "No paths provided.", ct);
            return;
        }

        int count = 0;
        foreach (var p in paths)
        {
            string fullPath = SafeResolvePath(p);
            if (DeleteItemInternal(fullPath)) count++;
        }

        await HttpResponse.WriteJsonAsync(stream, new { success = true, deletedCount = count }, 200, "OK", ct);
    }

    private bool DeleteItemInternal(string fullPath)
    {
        try
        {
            if (Settings.SendToRecycleBin)
            {
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    bool ok = SettingsService.MoveToRecycleBin(fullPath);
                    OnLog?.Invoke($"[DELETE] Moved to Recycle Bin: {fullPath}");
                    return ok;
                }
            }
            else
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    OnLog?.Invoke($"[DELETE] Deleted permanently: {fullPath}");
                    return true;
                }
                if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                    OnLog?.Invoke($"[DELETE] Folder deleted permanently: {fullPath}");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[DELETE ERROR] Could not delete {fullPath}: {ex.Message}");
        }
        return false;
    }

    private async Task HandleRenameAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string? oldPath = null;
        string? newName = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("oldPath", out var opElem)) oldPath = opElem.GetString();
            if (doc.RootElement.TryGetProperty("newName", out var nnElem)) newName = nnElem.GetString();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newName))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "oldPath and newName are required.", ct);
            return;
        }

        string fullOldPath = SafeResolvePath(oldPath);
        newName = Path.GetFileName(newName);
        string? parent = Path.GetDirectoryName(fullOldPath);

        if (string.IsNullOrEmpty(parent))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Invalid parent directory.", ct);
            return;
        }

        string fullNewPath = Path.Combine(parent, newName);

        if (File.Exists(fullOldPath))
        {
            File.Move(fullOldPath, fullNewPath);
            OnLog?.Invoke($"[RENAME] File renamed: {fullOldPath} -> {fullNewPath}");
            await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
            return;
        }

        if (Directory.Exists(fullOldPath))
        {
            Directory.Move(fullOldPath, fullNewPath);
            OnLog?.Invoke($"[RENAME] Directory renamed: {fullOldPath} -> {fullNewPath}");
            await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
            return;
        }

        await HttpResponse.WriteStatusAsync(stream, 404, "Not Found", "Item not found.", ct);
    }

    private string SafeResolvePath(string? path)
    {
        string normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory)) + Path.DirectorySeparatorChar;

        if (string.IsNullOrWhiteSpace(path))
        {
            return normRoot;
        }

        string full;
        if (Path.IsPathRooted(path))
        {
            full = Path.GetFullPath(path);
        }
        else
        {
            string rel = path.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            full = Path.GetFullPath(Path.Combine(normRoot, rel));
        }

        if (Settings.AccessMode == "FullComputer")
        {
            return full;
        }

        // בדיקה קפדנית של גבולות הספריה המשותפת כולל מפריד נתיב למניעת גישה לתיקיות אחיות
        string checkPath = Path.TrimEndingDirectorySeparator(full);
        string rootWithoutSep = Path.TrimEndingDirectorySeparator(normRoot);

        if (!checkPath.Equals(rootWithoutSep, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
        {
            OnLog?.Invoke($"[SECURITY] Path traversal blocked: {path} attempting to escape {normRoot}");
            throw new UnauthorizedAccessException("Access outside shared root is forbidden.");
        }

        return full;
    }

    private async Task HandleSecurePageAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string token = req.Query.TryGetValue("token", out var t) ? t : "";
        var item = SecureTransferService.GetTransfer(token);
        if (item == null)
        {
            string notFoundHtml = "<!DOCTYPE html><html dir='rtl' lang='he'><head><meta charset='utf-8'><title>קישור לא נמצא</title><style>body{background:#1F1F1F;color:#FFFFFF;font-family:system-ui;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}.card{background:#2B2B2B;padding:36px;border-radius:16px;text-align:center;max-width:440px;border:1px solid #383838;}</style></head><body><div class='card'><h2 style='color:#E11D48; margin-top:0;'>קישור לא נמצא</h2><p style='color:#B0B0B0;'>הקישור שביקשת אינו קיים, בוטל על ידי השולח, או שנמחק.</p></div></body></html>";
            await HttpResponse.WriteTextAsync(stream, notFoundHtml, "text/html; charset=utf-8", 404, "Not Found", ct);
            return;
        }

        if (item.IsExpired)
        {
            string expiredHtml = "<!DOCTYPE html><html dir='rtl' lang='he'><head><meta charset='utf-8'><title>הקישור פג תוקף</title><style>body{background:#1F1F1F;color:#FFFFFF;font-family:system-ui;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}.card{background:#2B2B2B;padding:36px;border-radius:16px;text-align:center;max-width:440px;border:1px solid #383838;}</style></head><body><div class='card'><h2 style='color:#F59E0B; margin-top:0;'>הקישור פג תוקף</h2><p style='color:#B0B0B0;'>תוקף הקישור פג או שהושגה מגבלת ההורדות המרבית.</p></div></body></html>";
            await HttpResponse.WriteTextAsync(stream, expiredHtml, "text/html; charset=utf-8", 410, "Gone", ct);
            return;
        }

        string prefillPin = req.Query.TryGetValue("pin", out var p) ? p : "";
        string sizeText = item.FileSizeBytes > 1024 * 1024 * 1024
            ? $"{item.FileSizeBytes / (1024.0 * 1024 * 1024):F2} GB"
            : item.FileSizeBytes > 1024 * 1024
            ? $"{item.FileSizeBytes / (1024.0 * 1024):F1} MB"
            : $"{Math.Max(1, item.FileSizeBytes / 1024)} KB";

        string expText = item.ExpiresAt == DateTime.MaxValue
            ? "ללא תפוגה"
            : $"{Math.Max(1, (int)(item.ExpiresAt - DateTime.Now).TotalMinutes)} דקות";

        string pageHtml = $@"<!DOCTYPE html>
<html dir='rtl' lang='he'>
<head>
    <meta charset='utf-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>שיתוף קבצים מאובטח | {System.Web.HttpUtility.HtmlEncode(item.FileName)}</title>
    <style>
        * {{ box-sizing: border-box; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; }}
        body {{ background: #1F1F1F; color: #FFFFFF; display: flex; align-items: center; justify-content: center; min-height: 100vh; margin: 0; padding: 20px; }}
        .card {{ background: #2B2B2B; border: 1px solid #383838; border-radius: 16px; padding: 36px 30px; max-width: 480px; width: 100%; box-shadow: 0 25px 50px -12px rgba(0,0,0,0.5); text-align: center; }}
        .icon-box {{ width: 56px; height: 56px; border-radius: 12px; background: rgba(0,120,212,0.12); border: 1px solid rgba(0,120,212,0.3); display: inline-flex; align-items: center; justify-content: center; margin-bottom: 16px; }}
        h1 {{ font-size: 20px; margin: 0 0 8px 0; word-break: break-all; color: #FFFFFF; font-weight: 600; }}
        .badge {{ display: inline-block; background: #252525; border: 1px solid #383838; padding: 6px 14px; border-radius: 999px; font-size: 13px; color: #388BE8; font-weight: 500; margin-bottom: 22px; }}
        .input-group {{ text-align: right; margin-bottom: 20px; }}
        label {{ font-size: 13px; font-weight: 600; color: #B0B0B0; margin-bottom: 6px; display: block; }}
        input[type='text'] {{ width: 100%; height: 46px; background: #1F1F1F; border: 1px solid #383838; border-radius: 10px; padding: 0 14px; font-size: 16px; font-weight: 600; color: #388BE8; text-align: center; letter-spacing: 2px; outline: none; }}
        input:focus {{ border-color: #0078D4; box-shadow: 0 0 0 3px rgba(0,120,212,0.25); }}
        .btn {{ width: 100%; height: 48px; background: #0078D4; color: white; border: none; border-radius: 10px; font-size: 15px; font-weight: 600; cursor: pointer; display: flex; align-items: center; justify-content: center; gap: 8px; text-decoration: none; transition: background 0.2s; }}
        .btn:hover {{ background: #0067C0; }}
        .footer {{ margin-top: 24px; font-size: 12px; color: #737373; border-top: 1px solid #383838; padding-top: 16px; display: flex; justify-content: space-between; }}
    </style>
</head>
<body>
    <div class='card'>
        <div class='icon-box'>
            <svg width='28' height='28' viewBox='0 0 24 24' fill='none' stroke='#388BE8' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
                <rect x='3' y='11' width='18' height='11' rx='2' ry='2'></rect>
                <path d='M7 11V7a5 5 0 0 1 10 0v4'></path>
            </svg>
        </div>
        <h1>{System.Web.HttpUtility.HtmlEncode(item.FileName)}</h1>
        <div class='badge'>גודל: {sizeText} | תפוגה: {expText}</div>

        <form method='GET' action='/secure/download'>
            <input type='hidden' name='token' value='{item.Token}'>
            <div class='input-group'>
                <label for='pin'>קוד אימות PIN שהתקבל מהשולח:</label>
                <input type='text' id='pin' name='pin' value='{System.Web.HttpUtility.HtmlEncode(prefillPin)}' placeholder='הזן קוד PIN' required autocomplete='off'>
            </div>
            <button type='submit' class='btn'>
                <svg width='18' height='18' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'>
                    <path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'></path>
                    <polyline points='7 10 12 15 17 10'></polyline>
                    <line x1='12' y1='15' x2='12' y2='3'></line>
                </svg>
                הורד קובץ מאובטח
            </button>
        </form>

        <div class='footer'>
            <span>מוגן קוד אימות PIN</span>
            <span>EasyShare PRO Enterprise</span>
        </div>
    </div>
</body>
</html>";
        await HttpResponse.WriteTextAsync(stream, pageHtml, "text/html; charset=utf-8", 200, "OK", ct);
    }

    private async Task HandleSecureDownloadAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string token = req.Query.TryGetValue("token", out var t) ? t : "";
        var item = SecureTransferService.GetTransfer(token);
        if (item == null)
        {
            await HttpResponse.WriteStatusAsync(stream, 404, "Not Found", "Transfer not found.", ct);
            return;
        }

        if (item.IsExpired)
        {
            await HttpResponse.WriteStatusAsync(stream, 410, "Gone", "Link has expired or download limit reached.", ct);
            return;
        }

        string pin = req.Query.TryGetValue("pin", out var p) ? p.Trim() : "";
        if (!string.Equals(item.PinCode, pin, StringComparison.OrdinalIgnoreCase))
        {
            SecurityService.RecordFailedAttempt(req.ClientIp);
            string failHtml = "<!DOCTYPE html><html dir='rtl' lang='he'><head><meta charset='utf-8'><title>קוד PIN שגוי</title><style>body{background:#1F1F1F;color:#FFFFFF;font-family:system-ui;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;}.card{background:#2B2B2B;padding:36px;border-radius:16px;text-align:center;max-width:440px;border:1px solid #383838;}.btn{display:inline-block;margin-top:20px;background:#0078D4;color:white;padding:12px 24px;border-radius:10px;text-decoration:none;font-weight:600;font-size:14px;}</style></head><body><div class='card'><h2 style='color:#E11D48; margin-top:0;'>קוד אימות PIN שגוי</h2><p style='color:#B0B0B0; font-size:14px;'>קוד האימות שהוזן אינו תואם. אנא ודא את הקוד מול השולח.</p><a class='btn' href='javascript:history.back()'>חזרה וניסיון חוזר</a></div></body></html>";
            await HttpResponse.WriteTextAsync(stream, failHtml, "text/html; charset=utf-8", 401, "Unauthorized", ct);
            return;
        }

        if (!File.Exists(item.FilePath))
        {
            await HttpResponse.WriteStatusAsync(stream, 404, "Not Found", "Target file is missing on host.", ct);
            return;
        }

        // הגדלת מונה ההורדות באופן אטומי
        int currentCount = item.IncrementDownloadCount();
        if (item.MaxDownloads > 0 && currentCount > item.MaxDownloads)
        {
            await HttpResponse.WriteStatusAsync(stream, 410, "Gone", "Link has expired or download limit reached.", ct);
            return;
        }
        OnLog?.Invoke($"[SECURE DOWNLOAD] {req.ClientIp} downloaded {item.FileName} (Count: {currentCount}/{item.MaxDownloads})");

        var fi = new FileInfo(item.FilePath);
        var activeConn = LiveNetworkMonitorService.Instance.RegisterConnection(req.ClientIp, item.FileName, fi.Length);
        try
        {
            string contentType = MimeTypes.GetMimeType(item.FileName);
            string encodedName = Uri.EscapeDataString(item.FileName);
            var secureDlHeaders = new Dictionary<string, string>
            {
                { "Content-Disposition", $@"attachment; filename=""{encodedName}""; filename*=UTF-8''{encodedName}" }
            };
            await HttpResponse.WriteFileAsync(stream, item.FilePath, contentType, req.ByteRange, secureDlHeaders, ct);
        }
        finally
        {
            LiveNetworkMonitorService.Instance.UnregisterConnection(activeConn.ConnectionId);

            // אם ההורדה הגיעה למקסימום המותר (Burn on First View) ומדובר בארכיון זמני
            if (item.MaxDownloads > 0 && item.DownloadCount >= item.MaxDownloads)
            {
                if (!string.IsNullOrEmpty(item.TempZipFilePath) && File.Exists(item.TempZipFilePath))
                {
                    SecureTransferService.SecureZeroFillShred(item.TempZipFilePath);
                    OnLog?.Invoke($"[BURN ON FIRST VIEW] Shredded temporary file: {item.FileName}");
                }
            }
        }
    }

    private async Task HandleSearchAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string query = req.Query.TryGetValue("q", out var q) ? q.Trim() : "";
        string subPath = req.Query.TryGetValue("path", out var p) ? p.Trim() : "";

        if (string.IsNullOrWhiteSpace(query))
        {
            await HttpResponse.WriteJsonAsync(stream, new { query = "", total = 0, items = new List<object>() }, 200, "OK", ct);
            return;
        }

        string targetDir = SafeResolvePath(subPath);
        if (!Directory.Exists(targetDir))
        {
            await HttpResponse.WriteStatusAsync(stream, 404, "Not Found", "Directory does not exist.", ct);
            return;
        }

        var dirInfo = new DirectoryInfo(targetDir);
        var items = new List<object>();

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            foreach (var fsi in dirInfo.EnumerateFileSystemInfos("*", options))
            {
                if (ct.IsCancellationRequested) break;
                if (!Settings.ShowHiddenFiles && (fsi.Attributes.HasFlag(FileAttributes.Hidden) || fsi.Name.StartsWith('.')))
                {
                    continue;
                }

                if (fsi.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    bool isDir = (fsi.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                    long size = isDir ? 0L : ((FileInfo)fsi).Length;
                    string mime = isDir ? "inode/directory" : MimeTypes.GetMimeType(fsi.FullName);

                    items.Add(new
                    {
                        name = fsi.Name,
                        path = fsi.FullName,
                        relativePath = GetDisplayPath(fsi.FullName),
                        isDirectory = isDir,
                        size = size,
                        modifiedDate = fsi.LastWriteTimeUtc.ToString("o"),
                        mimeType = mime
                    });

                    if (items.Count >= 200) break;
                }
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[SEARCH ERROR] {ex.Message}");
        }

        var res = new
        {
            query = query,
            total = items.Count,
            items = items
        };
        await HttpResponse.WriteJsonAsync(stream, res, 200, "OK", ct);
    }

    private async Task HandleGetMessagesAsync(Stream stream, CancellationToken ct)
    {
        var list = GetRecentMessages();
        await HttpResponse.WriteJsonAsync(stream, new { messages = list }, 200, "OK", ct);
    }

    private async Task HandlePostMessageAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string sender = "";
        string text = "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("sender", out var sElem)) sender = sElem.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("text", out var tElem)) text = tElem.GetString() ?? "";
        }
        catch { }

        if (string.IsNullOrWhiteSpace(sender))
        {
            sender = $"Client ({req.ClientIp})";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Message text is required.", ct);
            return;
        }

        AddMessage(sender, text);
        await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
    }

    private async Task HandleUploadChunkAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        if (!req.Query.TryGetValue("uploadId", out var uploadId) || string.IsNullOrWhiteSpace(uploadId))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing 'uploadId' parameter.", ct);
            return;
        }

        if (!req.Query.TryGetValue("chunkIndex", out var chunkIdxStr) || !int.TryParse(chunkIdxStr, out int chunkIndex))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing or invalid 'chunkIndex'.", ct);
            return;
        }

        if (!req.Query.TryGetValue("totalChunks", out var totalChunksStr) || !int.TryParse(totalChunksStr, out int totalChunks) || totalChunks <= 0)
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing or invalid 'totalChunks'.", ct);
            return;
        }

        string fileName = req.Query.TryGetValue("fileName", out var fn) && !string.IsNullOrWhiteSpace(fn) ? fn : "uploaded_file";
        string targetSubPath = req.Query.TryGetValue("path", out var p) ? p : "";

        string chunkDir = Path.Combine(Path.GetTempPath(), "EasyShareChunks", uploadId);
        Directory.CreateDirectory(chunkDir);
        string chunkFile = Path.Combine(chunkDir, $"chunk_{chunkIndex:D6}.part");

        await using (var fs = new FileStream(chunkFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
        {
            await req.BodyStream.CopyToAsync(fs, ct);
        }

        bool isComplete = false;
        string? finalPath = null;

        lock (string.Intern(uploadId))
        {
            var chunkFiles = Directory.GetFiles(chunkDir, "chunk_*.part");
            if (chunkFiles.Length >= totalChunks)
            {
                string baseDir = SafeResolvePath(targetSubPath);
                string cleanRel = fileName.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
                finalPath = Path.GetFullPath(Path.Combine(baseDir, cleanRel));
                string? targetFolder = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(targetFolder)) Directory.CreateDirectory(targetFolder);

                using (var destStream = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: false))
                {
                    for (int i = 0; i < totalChunks; i++)
                    {
                        string partPath = Path.Combine(chunkDir, $"chunk_{i:D6}.part");
                        if (File.Exists(partPath))
                        {
                            using (var partStream = File.OpenRead(partPath))
                            {
                                partStream.CopyTo(destStream);
                            }
                        }
                    }
                }

                try { Directory.Delete(chunkDir, true); } catch { }
                isComplete = true;
                OnLog?.Invoke($"[CHUNK UPLOAD] Completed file: {cleanRel} ({totalChunks} chunks)");
            }
        }

        if (isComplete && finalPath != null)
        {
            await HttpResponse.WriteJsonAsync(stream, new { success = true, completed = true, fileName = Path.GetFileName(finalPath) }, 200, "OK", ct);
        }
        else
        {
            await HttpResponse.WriteJsonAsync(stream, new { success = true, completed = false, chunkIndex = chunkIndex, totalChunks = totalChunks }, 200, "OK", ct);
        }
    }

    private async Task HandleBlacklistAddAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string? ip = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ip", out var ipElem)) ip = ipElem.GetString();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(ip))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "IP address is required.", ct);
            return;
        }

        BlacklistIp(ip);
        await HttpResponse.WriteJsonAsync(stream, new { success = true, ip = ip.Trim() }, 200, "OK", ct);
    }

    private async Task HandleBlacklistRemoveAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string? ip = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ip", out var ipElem)) ip = ipElem.GetString();
        }
        catch { }

        if (string.IsNullOrWhiteSpace(ip))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "IP address is required.", ct);
            return;
        }

        UnblacklistIp(ip);
        await HttpResponse.WriteJsonAsync(stream, new { success = true, ip = ip.Trim() }, 200, "OK", ct);
    }

    private string GetDisplayPath(string fullPath)
    {
        if (Settings.AccessMode == "FullComputer")
        {
            return fullPath;
        }
        return Path.GetRelativePath(RootDirectory, fullPath).Replace('\\', '/');
    }

    private async Task HandlePeerDropAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string targetDir = Path.Combine(RootDirectory, "Received_Drops");
        Directory.CreateDirectory(targetDir);

        string senderPeerId = req.GetHeader("X-Sender-PeerId") ?? "Unknown";
        string senderDeviceName = req.GetHeader("X-Sender-DeviceName") ?? senderPeerId;
        try
        {
            senderDeviceName = Uri.UnescapeDataString(senderDeviceName);
        }
        catch { }

        var savedFiles = await MultipartParser.ParseAndSaveFilesAsync(req, targetDir, ct);
        foreach (var file in savedFiles)
        {
            PeerDiscovery.NotifyDirectFileReceived(senderPeerId, senderDeviceName, file.FileName, file.SavedPath);
        }

        OnLog?.Invoke($"[PEER DROP] נתקבלו {savedFiles.Count} קבצים מעמית {senderDeviceName} ({senderPeerId}) ונשמרו בתיקיית Received_Drops");
        await HttpResponse.WriteJsonAsync(stream, new { success = true, count = savedFiles.Count, files = savedFiles, targetDir = "Received_Drops" }, 200, "OK", ct);
    }

    private async Task HandlePeerDirectMessageAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string senderPeerId = "";
        string senderName = "";
        string text = "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("senderPeerId", out var spElem)) senderPeerId = spElem.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("senderName", out var snElem)) senderName = snElem.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("text", out var tElem)) text = tElem.GetString() ?? "";
        }
        catch { }

        if (string.IsNullOrWhiteSpace(text))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing text", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(senderName)) senderName = senderPeerId;
        if (string.IsNullOrWhiteSpace(senderName)) senderName = "עמית ברשת";

        AddMessage($"{senderName} ({senderPeerId})", text);
        PeerDiscovery.NotifyDirectMessageReceived(senderPeerId, senderName, text);

        await HttpResponse.WriteJsonAsync(stream, new { success = true }, 200, "OK", ct);
    }

    private async Task HandlePeerSendAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string targetPeerId = "";
        string message = "";
        List<string>? files = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("targetPeerId", out var tpElem)) targetPeerId = tpElem.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("message", out var mElem)) message = mElem.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("files", out var fElem) && fElem.ValueKind == JsonValueKind.Array)
            {
                files = fElem.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList()!;
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(targetPeerId))
        {
            await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing targetPeerId", ct);
            return;
        }

        var peer = PeerDiscovery.FindPeer(targetPeerId);
        if (peer == null)
        {
            await PeerDiscovery.QueryPeerAsync(targetPeerId);
            await HttpResponse.WriteJsonAsync(stream, new { success = false, message = $"העמית {targetPeerId} אינו מקוון כעת." }, 404, "Not Found", ct);
            return;
        }

        if (files != null && files.Count > 0)
        {
            var resolvedFiles = files.Select(f => SafeResolvePath(f)).Where(File.Exists).ToList();
            var (dropOk, dropMsg) = await PeerDiscovery.DropFilesToPeerAsync(peer, resolvedFiles, ct);
            await HttpResponse.WriteJsonAsync(stream, new { success = dropOk, message = dropMsg }, dropOk ? 200 : 500, dropOk ? "OK" : "Error", ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            var (msgOk, msgResult) = await PeerDiscovery.SendDirectMessageAsync(peer, message, ct);
            await HttpResponse.WriteJsonAsync(stream, new { success = msgOk, message = msgResult }, msgOk ? 200 : 500, msgOk ? "OK" : "Error", ct);
            return;
        }

        await HttpResponse.WriteStatusAsync(stream, 400, "Bad Request", "Missing message or files to send.", ct);
    }

    private async Task HandlePeerChatHistoryAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string peerId = "";
        if (req.Query.TryGetValue("targetPeerId", out var t1)) peerId = t1;
        else if (req.Query.TryGetValue("peerId", out var t2)) peerId = t2;

        if (string.IsNullOrWhiteSpace(peerId))
        {
            await HttpResponse.WriteJsonAsync(stream, new { error = "targetPeerId is required." }, 400, "Bad Request", ct);
            return;
        }

        var messages = PeerDiscovery.GetChatHistory(peerId);
        await HttpResponse.WriteJsonAsync(stream, new { peerId, count = messages.Count, messages }, 200, "OK", ct);
    }

    private async Task HandlePeerChatClearAsync(HttpRequest req, Stream stream, CancellationToken ct)
    {
        string body = await req.ReadBodyAsStringAsync(ct);
        string targetPeerId = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("targetPeerId", out var tElem)) targetPeerId = tElem.GetString() ?? "";
            else if (doc.RootElement.TryGetProperty("peerId", out var pElem)) targetPeerId = pElem.GetString() ?? "";
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(targetPeerId))
        {
            PeerDiscovery.ClearChatHistory(targetPeerId);
        }

        await HttpResponse.WriteJsonAsync(stream, new { success = true, peerId = targetPeerId }, 200, "OK", ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        TunnelService.Stop();

        try
        {
            PeerDiscovery.Stop();
        }
        catch { }

        try
        {
            _mdnsService?.Dispose();
            _mdnsService = null;
            _ = Core.UpnpPortForwarder.DeletePortMappingAsync(Port);
        }
        catch { }

        OnLog?.Invoke("[SERVER] Stopped.");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
        TunnelService.Dispose();
        PeerDiscovery.Dispose();
    }
}
