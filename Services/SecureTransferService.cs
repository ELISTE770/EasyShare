using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EasyShare.Http;

namespace EasyShare.Services;

public class SecureTransferItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Token { get; set; } = string.Empty;
    public string PinCode { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ExpiresAt { get; set; }
    public int MaxDownloads { get; set; } = 1; // 1 = חד פעמי (השמדה עצמית), 0 = ללא הגבלה
    private int _downloadCount = 0;
    public int DownloadCount
    {
        get => Volatile.Read(ref _downloadCount);
        set => Interlocked.Exchange(ref _downloadCount, value);
    }
    public int IncrementDownloadCount() => Interlocked.Increment(ref _downloadCount);
    public bool IsCancelled { get; set; } = false;

    public bool IsFolder { get; set; } = false;
    public string? TempZipFilePath { get; set; }

    public string Channel { get; set; } = "Tunnel"; // "LAN", "Tunnel", "Cloud"
    public string? ShareUrl { get; set; }
    public string? CloudDirectUrl { get; set; }
    public byte[]? EncryptedPayloadCache { get; set; }

    public Dictionary<string, int> FailedAttemptsByIp { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> BlockedIps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public const int MaxFailedAttemptsPerIp = 6;

    public bool IsExpired => IsCancelled || (ExpiresAt != DateTime.MaxValue && DateTime.Now > ExpiresAt) || (MaxDownloads > 0 && DownloadCount >= MaxDownloads);

    public string CancelButtonText => Core.LocalizationService.Instance.CurrentLanguage == Core.LocalizationService.LanguageHebrew ? "בטל קישור ✕" : "Cancel Link ✕";

    public string FileIcon => IsFolder ? "📁" : (FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "📦" : (FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "📕" : (FileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || FileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) ? "🎬" : (FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "🖼️" : "📄"))));

    public string FormattedSize
    {
        get
        {
            if (FileSizeBytes <= 0) return "—";
            if (FileSizeBytes > 1024 * 1024 * 1024) return $"{FileSizeBytes / (1024.0 * 1024 * 1024):F2} GB";
            if (FileSizeBytes > 1024 * 1024) return $"{FileSizeBytes / (1024.0 * 1024):F1} MB";
            return $"{Math.Max(1, FileSizeBytes / 1024)} KB";
        }
    }

    public string FormattedCreatedAt => CreatedAt.ToString("dd/MM/yyyy HH:mm");

    public string FormattedExpiresAt => ExpiresAt == DateTime.MaxValue ? (Core.LocalizationService.Instance.CurrentLanguage == Core.LocalizationService.LanguageHebrew ? "ללא תפוגה" : "No Expiration") : ExpiresAt.ToString("dd/MM/yyyy HH:mm");

    public string SecurityDisplay => string.IsNullOrWhiteSpace(PinCode) ? (Core.LocalizationService.Instance.CurrentLanguage == Core.LocalizationService.LanguageHebrew ? "🔓 פתוח" : "🔓 Open") : $"🔒 PIN: {PinCode}";

    public string EffectiveUrl => !string.IsNullOrWhiteSpace(ShareUrl) ? ShareUrl : (!string.IsNullOrWhiteSpace(CloudDirectUrl) ? CloudDirectUrl : "—");

    public string ChannelDisplay
    {
        get
        {
            bool isHe = Core.LocalizationService.Instance.CurrentLanguage == Core.LocalizationService.LanguageHebrew;
            return Channel switch
            {
                "LAN" => isHe ? "🏠 רשת מקומית (LAN)" : "🏠 Local Network (LAN)",
                "Tunnel" => isHe ? "🌐 מנהרת Cloudflare" : "🌐 Cloudflare Tunnel",
                "DirectCloud" => isHe ? "⚡ ענן ציבורי מהיר" : "⚡ Direct Cloud",
                "Cloud" => isHe ? "☁️ ענן מוצפן אפמראלי" : "☁️ Ephemeral Cloud Relay",
                "OneDrive" => "📁 OneDrive",
                "GoogleDrive" => "📁 Google Drive",
                _ => Channel
            };
        }
    }

    public string StatusBadgeColor
    {
        get
        {
            if (IsCancelled) return "#EF4444"; // Red
            if (ExpiresAt != DateTime.MaxValue && DateTime.Now > ExpiresAt) return "#F59E0B"; // Amber
            if (MaxDownloads > 0 && DownloadCount >= MaxDownloads) return "#F59E0B";
            return "#10B981"; // Green
        }
    }

    public string StatusDescription
    {
        get
        {
            bool isHe = Core.LocalizationService.Instance.CurrentLanguage == Core.LocalizationService.LanguageHebrew;
            if (Channel == "OneDrive" || Channel == "GoogleDrive") 
                return isHe ? $"מסונכרן בענן ({Channel})" : $"Synced to cloud ({Channel})";
            if (Channel == "DirectCloud") 
                return isHe ? "הועלה לענן ישיר (קישור ציבורי)" : "Uploaded to direct cloud (Public link)";
            if (IsCancelled) 
                return isHe ? "בוטל ידנית" : "Cancelled manually";
            if (MaxDownloads > 0 && DownloadCount >= MaxDownloads) 
                return isHe ? $"הושלם ({DownloadCount}/{MaxDownloads} הורדות)" : $"Completed ({DownloadCount}/{MaxDownloads} downloads)";
            if (ExpiresAt != DateTime.MaxValue && DateTime.Now > ExpiresAt) 
                return isHe ? "פג תוקף" : "Expired";
            string dlInfo = MaxDownloads > 0 
                ? (isHe ? $" | נותרו {Math.Max(0, MaxDownloads - DownloadCount)} הורדות" : $" | {Math.Max(0, MaxDownloads - DownloadCount)} downloads remaining") 
                : (isHe ? " | הורדות ללא הגבלה" : " | Unlimited downloads");
            if (ExpiresAt == DateTime.MaxValue) 
                return isHe ? $"פעיל לתמיד (ללא תפוגה{dlInfo})" : $"Active permanently (No expiration{dlInfo})";
            var remaining = ExpiresAt - DateTime.Now;
            return isHe ? $"פעיל (נותרו {Math.Max(1, (int)remaining.TotalMinutes)} דקות{dlInfo})" : $"Active ({Math.Max(1, (int)remaining.TotalMinutes)} mins left{dlInfo})";
        }
    }
}

/// <summary>
/// מנהל את העברות הקבצים המאובטחות (Secure Transfer & Cloud Drop):
/// תמיכה ברשת מקומית (LAN), מנהרת Cloudflare HTTPS, והעלאה לענן אפמראלי מוצפן (Zero-Knowledge Cloud Relay).
/// </summary>
public sealed class SecureTransferService
{
    private static readonly ConcurrentDictionary<string, SecureTransferItem> _transfers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IpAttemptRecord> _generalIpAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _manuallyBlockedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _activeSessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _sessionDuration = TimeSpan.FromHours(24);
    private readonly TimeSpan _lockoutDuration = TimeSpan.FromMinutes(15);
    private const int MaxFailedAttempts = 6;
    private static readonly System.Threading.Timer _cleanupTimer = new(_ => CleanExpiredTransfersAndTempFiles(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15));

    public static event Action? OnTransfersChanged;
    private static readonly ConcurrentDictionary<string, DateTime> _revokedTokens = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsTokenRevoked(string token) => !string.IsNullOrWhiteSpace(token) && _revokedTokens.ContainsKey(token);

    public static SecureTransferService Instance { get; } = new();

    public event Action<string, string>? OnIpBlocked;
    public event Action? OnBlockedListChanged;
    public string? ActivePin { get; private set; }
    public bool IsPinRequired => !string.IsNullOrEmpty(ActivePin);

    public SecureTransferService(string? initialPin = null)
    {
        ActivePin = initialPin;
    }

    public void SetCustomPin(string? pin)
    {
        ActivePin = string.IsNullOrWhiteSpace(pin) ? null : pin.Trim();
        RevokeAllSessions();
    }

    public string CreateSession()
    {
        string token = GenerateRandomToken(32);
        _activeSessions[token] = DateTime.UtcNow.Add(_sessionDuration);
        return token;
    }

    public bool IsValidSession(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        if (_activeSessions.TryGetValue(token, out var expiry))
        {
            if (DateTime.UtcNow < expiry) return true;
            _activeSessions.TryRemove(token, out _);
        }
        return false;
    }

    public void RevokeAllSessions() => _activeSessions.Clear();

    public string GenerateNewRandomPin()
    {
        ActivePin = RandomNumberGenerator.GetInt32(10000000, 99999999).ToString();
        return ActivePin;
    }

    public static string GenerateRandomPin(int length = 8)
    {
        int min = (int)Math.Pow(10, length - 1);
        int max = (int)Math.Pow(10, length) - 1;
        return RandomNumberGenerator.GetInt32(min, max).ToString();
    }

    public static string GenerateRandomToken(int length = 12)
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(length);
        foreach (byte b in bytes) sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    #region יצירת העברה מאובטחת (LAN / Tunnel / Cloud)

    public async Task<SecureTransferItem> CreateTransferAsync(
        string targetPath,
        string? pinCode = null,
        int expirationMinutes = 60,
        int maxDownloads = 1,
        string channel = "Tunnel",
        string? serverBaseUrl = null,
        CancellationToken ct = default)
    {
        bool isDir = Directory.Exists(targetPath);
        if (!isDir && !File.Exists(targetPath))
            throw new FileNotFoundException("הקובץ או התיקייה המבוקשים אינם קיימים", targetPath);

        if (string.IsNullOrWhiteSpace(pinCode))
            pinCode = GenerateRandomPin(8);

        string token = GenerateRandomToken(12);
        string effectivePath = targetPath;
        string displayName = Path.GetFileName(targetPath);
        long size = 0;
        string? tempZip = null;

        if (isDir)
        {
            var di = new DirectoryInfo(targetPath);
            displayName = $"{di.Name}.zip";
            string tempDir = Path.Combine(Path.GetTempPath(), "EasyShare_Transfers");
            Directory.CreateDirectory(tempDir);
            tempZip = Path.Combine(tempDir, $"{di.Name}_{token}.zip");

            if (File.Exists(tempZip)) File.Delete(tempZip);
            ZipFile.CreateFromDirectory(targetPath, tempZip, CompressionLevel.Fastest, false);

            effectivePath = tempZip;
            size = new FileInfo(tempZip).Length;
        }
        else
        {
            size = new FileInfo(targetPath).Length;
        }

        var item = new SecureTransferItem
        {
            Token = token,
            PinCode = pinCode.Trim(),
            FilePath = effectivePath,
            FileName = displayName,
            FileSizeBytes = size,
            IsFolder = isDir,
            TempZipFilePath = tempZip,
            CreatedAt = DateTime.Now,
            ExpiresAt = expirationMinutes <= 0 ? DateTime.MaxValue : DateTime.Now.AddMinutes(Math.Max(5, expirationMinutes)),
            MaxDownloads = maxDownloads,
            Channel = channel
        };

        // העלאה ישירה לענן ציבורי מהיר (קישור ישיר מיידי ללא צורך בדפדפן או הרשמה)
        if (channel == "DirectCloud")
        {
            string? directUrl = await UploadToDirectPublicCloudAsync(effectivePath, ct);
            if (!string.IsNullOrEmpty(directUrl))
            {
                item.CloudDirectUrl = directUrl;
                item.ShareUrl = directUrl;
            }
            else
            {
                throw new Exception("שגיאה בהעלאת הקובץ לענן הישיר. אנא ודא חיבור אינטרנט פעיל.");
            }
        }
        // הצפנה והעלאה לענן אפמראלי (Cloud Relay) אם נבחר ערוץ Cloud
        else if (channel == "Cloud")
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(effectivePath, ct);
            item.EncryptedPayloadCache = EncryptBytesWithPin(fileBytes, item.PinCode);

            string? cloudUrl = await UploadToCloudRelayAsync(item.EncryptedPayloadCache, token, ct);
            if (!string.IsNullOrEmpty(cloudUrl))
            {
                item.CloudDirectUrl = cloudUrl;
                item.ShareUrl = cloudUrl;
            }
            else
            {
                throw new Exception("שגיאה בהעלאת הקובץ המוצפן לשרת הענן האפמראלי.");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(serverBaseUrl))
            {
                item.ShareUrl = $"{serverBaseUrl.TrimEnd('/')}/secure?token={token}";
            }
        }

        _transfers[token] = item;
        OnTransfersChanged?.Invoke();
        return item;
    }

    public static SecureTransferItem? GetTransfer(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        return _transfers.TryGetValue(token, out var item) ? item : null;
    }

    public static List<SecureTransferItem> GetAllActiveTransfers()
    {
        return _transfers.Values.OrderByDescending(t => t.CreatedAt).ToList();
    }

    /// <summary>
    /// מבטל ומשמיד קישור שיתוף וטוקן גישה באופן מיידי.
    /// קריטי: הפעולה משמידה אך ורק את קישור השיתוף, טוקן הגישה וקובצי ה-ZIP הזמניים של ההעברה.
    /// הקובץ או התיקייה המקוריים על גבי המחשב (FilePath) נשארים שלמים ובטוחים לחלוטין ולעולם אינם נמחקים!
    /// </summary>
    public static bool RevokeTransfer(string token, bool removeFromList = false)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        _revokedTokens[token] = DateTime.UtcNow;

        if (_transfers.TryGetValue(token, out var item))
        {
            item.IsCancelled = true;

            // אם נוצר קובץ זיפ זמני עבור תיקייה בתיקיית ה-TEMP - נגרס באבטחה
            if (!string.IsNullOrEmpty(item.TempZipFilePath) && File.Exists(item.TempZipFilePath))
            {
                SecureZeroFillShred(item.TempZipFilePath);
                item.TempZipFilePath = null;
            }

            // שים לב: item.FilePath (הקובץ המקורי במחשב) אינו נמחק לעולם!
            if (removeFromList)
            {
                _transfers.TryRemove(token, out _);
            }

            OnTransfersChanged?.Invoke();
            return true;
        }

        return false;
    }

    /// <summary>
    /// משמיד את כל קישורי השיתוף הפעילים בבת אחת, מבלי לפגוע כלל בקבצים המקוריים במחשב.
    /// </summary>
    public static int RevokeAllTransfers(bool removeFromList = false)
    {
        int count = 0;
        var keys = _transfers.Keys.ToList();
        foreach (var k in keys)
        {
            if (RevokeTransfer(k, removeFromList)) count++;
        }
        return count;
    }

    public static void CancelTransfer(string token)
    {
        RevokeTransfer(token, removeFromList: false);
    }

    /// <summary>
    /// מנקה קובצי ZIP זמניים של שיתופים שפגו או שיתופים יתומים שהושארו בתיקיית ה-TEMP
    /// </summary>
    public static void CleanExpiredTransfersAndTempFiles()
    {
        try
        {
            var activeTempFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _transfers.Values)
            {
                if (item.IsExpired)
                {
                    if (!string.IsNullOrEmpty(item.TempZipFilePath) && File.Exists(item.TempZipFilePath))
                    {
                        SecureZeroFillShred(item.TempZipFilePath);
                        item.TempZipFilePath = null;
                    }
                }
                else if (!string.IsNullOrEmpty(item.TempZipFilePath))
                {
                    activeTempFiles.Add(Path.GetFullPath(item.TempZipFilePath));
                }
            }

            string tempDir = Path.Combine(Path.GetTempPath(), "EasyShare_Transfers");
            if (Directory.Exists(tempDir))
            {
                var files = Directory.GetFiles(tempDir, "*.zip");
                var now = DateTime.Now;
                foreach (var f in files)
                {
                    string full = Path.GetFullPath(f);
                    if (!activeTempFiles.Contains(full))
                    {
                        var fi = new FileInfo(f);
                        if (now - fi.CreationTime > TimeSpan.FromHours(2) || now - fi.LastWriteTime > TimeSpan.FromHours(2))
                        {
                            SecureZeroFillShred(f);
                        }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// מבצע מחיקה פיזית בטוחה (DoD 5220.22-M Secure Zero-Fill Shredding)
    /// על ידי דריסת כל הבייטים של הקובץ באפסים לפני המחיקה ממערכת הקבצים.
    /// </summary>
    public static void SecureZeroFillShred(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var fileInfo = new FileInfo(filePath);
            long length = fileInfo.Length;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                byte[] zeros = new byte[Math.Min(65536, (int)Math.Min(int.MaxValue, length))];
                long written = 0;
                while (written < length)
                {
                    int toWrite = (int)Math.Min(zeros.Length, length - written);
                    fs.Write(zeros, 0, toWrite);
                    written += toWrite;
                }
                fs.Flush();
            }

            File.Delete(filePath);
        }
        catch { }
    }

    public static SecureTransferItem RegisterExternalCloudTransfer(
        string fileName,
        string filePath,
        string channel,
        string shareUrl)
    {
        string token = GenerateRandomToken(12);
        var item = new SecureTransferItem
        {
            Token = token,
            PinCode = "ענן מאובטח",
            FilePath = filePath,
            FileName = fileName,
            CreatedAt = DateTime.Now,
            ExpiresAt = DateTime.Now.AddDays(30),
            MaxDownloads = 0,
            Channel = channel,
            ShareUrl = shareUrl
        };
        _transfers[token] = item;
        OnTransfersChanged?.Invoke();
        return item;
    }

    #endregion

    #region הצפנה ופענוח ב-AES-256 GCM

    public static byte[] EncryptBytesWithPin(byte[] plaintext, string pinCode)
    {
        byte[] salt = new byte[16];
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(salt);
        RandomNumberGenerator.Fill(nonce);

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(pinCode.Trim(), salt, 100_000, HashAlgorithmName.SHA256, 32);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];

        using (var aesGcm = new AesGcm(key, 16))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        using var ms = new MemoryStream();
        ms.Write(salt);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(ciphertext);
        return ms.ToArray();
    }

    public static byte[] DecryptBytesWithPin(byte[] payload, string pinCode)
    {
        if (payload.Length < 44) throw new ArgumentException("המידע המוצפן קצר מדי או פגום");

        byte[] salt = new byte[16];
        byte[] nonce = new byte[12];
        byte[] tag = new byte[16];
        byte[] ciphertext = new byte[payload.Length - 44];

        Array.Copy(payload, 0, salt, 0, 16);
        Array.Copy(payload, 16, nonce, 0, 12);
        Array.Copy(payload, 28, tag, 0, 16);
        Array.Copy(payload, 44, ciphertext, 0, ciphertext.Length);

        byte[] key = Rfc2898DeriveBytes.Pbkdf2(pinCode.Trim(), salt, 100_000, HashAlgorithmName.SHA256, 32);
        byte[] decrypted = new byte[ciphertext.Length];

        using (var aesGcm = new AesGcm(key, 16))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, decrypted);
        }

        return decrypted;
    }

    public static async Task<string?> UploadToCloudRelayAsync(byte[] encryptedPayload, string? transferToken = null, CancellationToken token = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            string safeUploadName = string.IsNullOrEmpty(transferToken) ? $"payload_{Guid.NewGuid():N}.bin" : $"payload_{transferToken}.bin";

            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(encryptedPayload), "file", safeUploadName);

            var response = await client.PostAsync("https://tmpfiles.org/api/v1/upload", form, token);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var dataElem) &&
                dataElem.TryGetProperty("url", out var urlElem))
            {
                string rawUrl = urlElem.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(rawUrl))
                {
                    return rawUrl.Replace("tmpfiles.org/", "tmpfiles.org/dl/");
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// מעלה קובץ ישירות לענן ציבורי מהיר עם קישור הורדה ישיר מיידי, ללא צורך בהרשמה, דפדפן או מפתח API.
    /// מנגנון שרידות משולש (tmpfiles.org direct dl -> catbox.moe -> litterbox).
    /// </summary>
    public static async Task<string?> UploadToDirectPublicCloudAsync(string filePath, CancellationToken ct = default)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) EasyShare/2.0");

        // נסיון 1: tmpfiles.org עם קישור הורדה ישיר מיידי (/dl/)
        try
        {
            using var form = new MultipartFormDataContent();
            await using var stream = File.OpenRead(filePath);
            var fileContent = new StreamContent(stream);
            form.Add(fileContent, "file", Path.GetFileName(filePath));

            var res = await httpClient.PostAsync("https://tmpfiles.org/api/v1/upload", form, ct);
            if (res.IsSuccessStatusCode)
            {
                string json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataElem) &&
                    dataElem.TryGetProperty("url", out var urlElem))
                {
                    string rawUrl = urlElem.GetString() ?? "";
                    if (!string.IsNullOrEmpty(rawUrl))
                    {
                        return rawUrl.Replace("tmpfiles.org/", "tmpfiles.org/dl/");
                    }
                }
            }
        }
        catch { }

        // נסיון 2: Catbox.moe (שירות שיתוף קבצים ישיר ופופולרי לקבצים עד 200MB)
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("fileupload"), "reqtype");
            await using var stream = File.OpenRead(filePath);
            var fileContent = new StreamContent(stream);
            form.Add(fileContent, "fileToUpload", Path.GetFileName(filePath));

            var res = await httpClient.PostAsync("https://catbox.moe/user/api.php", form, ct);
            if (res.IsSuccessStatusCode)
            {
                string url = (await res.Content.ReadAsStringAsync(ct)).Trim();
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
        }
        catch { }

        // נסיון 3: Litterbox (שירות אחסון אפמראלי מהיר ל-72 שעות של Catbox)
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("fileupload"), "reqtype");
            form.Add(new StringContent("72h"), "time");
            await using var stream = File.OpenRead(filePath);
            var fileContent = new StreamContent(stream);
            form.Add(fileContent, "fileToUpload", Path.GetFileName(filePath));

            var res = await httpClient.PostAsync("https://litterbox.catbox.moe/resources/internals/api.php", form, ct);
            if (res.IsSuccessStatusCode)
            {
                string url = (await res.Content.ReadAsStringAsync(ct)).Trim();
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }
            }
        }
        catch { }

        return null;
    }

    #endregion

    #region אימות כללי והגנת Brute-Force

    public bool IsIpBlocked(string ip, out string reason)
    {
        if (_manuallyBlockedIps.TryGetValue(ip, out var manualReason))
        {
            reason = $"כתובת IP {ip} נחסמה על ידי מנהל המערכת: {manualReason}";
            return true;
        }

        if (_generalIpAttempts.TryGetValue(ip, out var record))
        {
            if (record.LockedUntil.HasValue)
            {
                if (DateTime.UtcNow < record.LockedUntil.Value)
                {
                    var remaining = record.LockedUntil.Value - DateTime.UtcNow;
                    reason = $"IP {ip} is temporarily blocked ({remaining.Minutes}m {remaining.Seconds}s remaining).";
                    return true;
                }
                else
                {
                    _generalIpAttempts.TryRemove(ip, out _);
                }
            }
        }
        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// חוסם כתובת IP באופן יזום וקבוע (Kick &amp; Block).
    /// </summary>
    public void BlockIpManually(string ip, string reason = "נחסם ידנית על ידי מנהל המערכת")
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        string cleanIp = ip.Trim();
        _manuallyBlockedIps[cleanIp] = reason;
        OnIpBlocked?.Invoke(cleanIp, $"[MANUAL BLOCK] {reason}");
        OnBlockedListChanged?.Invoke();
    }

    /// <summary>
    /// מסיר חסימה מכתובת IP.
    /// </summary>
    public bool UnblockIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        string cleanIp = ip.Trim();
        bool removed = _manuallyBlockedIps.TryRemove(cleanIp, out _);
        _generalIpAttempts.TryRemove(cleanIp, out _);
        if (removed) OnBlockedListChanged?.Invoke();
        return removed;
    }

    /// <summary>
    /// מחזיר את כל כתובות ה-IP החסומות כעת במערכת.
    /// </summary>
    public List<KeyValuePair<string, string>> GetBlockedIpsWithReason()
    {
        return _manuallyBlockedIps.Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value)).ToList();
    }

    public void RecordFailedAttempt(string ip)
    {
        var record = _generalIpAttempts.AddOrUpdate(ip,
            _ => new IpAttemptRecord { FailedCount = 1, LastAttempt = DateTime.UtcNow },
            (_, existing) =>
            {
                existing.FailedCount++;
                existing.LastAttempt = DateTime.UtcNow;
                return existing;
            });

        if (record.FailedCount >= MaxFailedAttempts && !record.LockedUntil.HasValue)
        {
            record.LockedUntil = DateTime.UtcNow.Add(_lockoutDuration);
            string message = $"IP {ip} blocked after {record.FailedCount} failed attempts.";
            OnIpBlocked?.Invoke(ip, message);
        }
    }

    public void ResetAttempts(string ip) => _generalIpAttempts.TryRemove(ip, out _);
    public void ResetAllAttempts() => _generalIpAttempts.Clear();

    public bool ValidatePin(string ip, string? pin)
    {
        if (IsIpBlocked(ip, out _)) return false;
        if (!IsPinRequired) return true;

        bool isValid = string.Equals(ActivePin, pin?.Trim(), StringComparison.Ordinal);
        if (isValid) ResetAttempts(ip);
        else RecordFailedAttempt(ip);

        return isValid;
    }

    public static async Task StreamDirectoryAsZipAsync(
        Stream outputStream,
        string sourceDirectory,
        string zipFileName,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Directory '{sourceDirectory}' not found.");

        var customHeaders = new Dictionary<string, string>
        {
            { "Content-Disposition", $@"attachment; filename=""{Uri.EscapeDataString(zipFileName)}""" }
        };

        await HttpResponse.WriteHeadersAsync(outputStream, 200, "OK", "application/zip", null, customHeaders, ct);

        using (var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories);
            byte[] copyBuffer = new byte[65536];

            foreach (string file in files)
            {
                if (ct.IsCancellationRequested) break;
                string relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
                var entry = archive.CreateEntry(relativePath, CompressionLevel.Fastest);

                await using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                await using var entryStream = entry.Open();

                int read;
                while ((read = await fileStream.ReadAsync(copyBuffer, ct)) > 0)
                {
                    await entryStream.WriteAsync(copyBuffer.AsMemory(0, read), ct);
                }
            }
        }

        await outputStream.FlushAsync(ct);
    }

    #endregion

    private sealed class IpAttemptRecord
    {
        public int FailedCount { get; set; }
        public DateTime LastAttempt { get; set; }
        public DateTime? LockedUntil { get; set; }
    }
}
