using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// מייצג חיבור לקוח פעיל להורדת קובץ או סטרימינג.
/// </summary>
public class ActiveClientConnection
{
    public string ConnectionId { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string ClientIp { get; set; } = "127.0.0.1";
    public string CountryFlag { get; set; } = "LAN";
    public string TargetFileName { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long BytesTransferred { get; set; }
    public double ProgressPercentage => TotalBytes > 0 ? Math.Min(100.0, (double)BytesTransferred / TotalBytes * 100.0) : 0;
    public DateTime StartTime { get; set; } = DateTime.Now;
    public CancellationTokenSource Cts { get; } = new();

    public void Abort()
    {
        try { Cts.Cancel(); } catch { }
    }
}

/// <summary>
/// מנהל ומנטר את כל ההורדות, הסטרימינג ותעבורת הרשת בשרת בזמן אמת.
/// מחשב קצב העלאה (Upload Speed in Mbps) ומאפשר שליטה וניתוק חיבורים פעילים.
/// </summary>
public sealed class LiveNetworkMonitorService
{
    public static LiveNetworkMonitorService Instance { get; } = new();

    private readonly ConcurrentDictionary<string, ActiveClientConnection> _activeConnections = new();
    private long _bytesInLastSecond = 0;
    private long _totalBytesTransferred = 0;
    private readonly System.Timers.Timer _speedTimer;

    public double CurrentUploadSpeedMbps { get; private set; }
    public long TotalBytesTransferred => _totalBytesTransferred;

    public string FormattedTotalTransferred
    {
        get
        {
            double mb = _totalBytesTransferred / (1024.0 * 1024.0);
            if (mb >= 1024.0) return $"{mb / 1024.0:F2} GB";
            return $"{mb:F1} MB";
        }
    }

    public event Action<double>? OnSpeedUpdated; // Mbps
    public event Action? OnConnectionsChanged;

    public LiveNetworkMonitorService()
    {
        _speedTimer = new System.Timers.Timer(1000);
        _speedTimer.Elapsed += (s, e) =>
        {
            long bytes = Interlocked.Exchange(ref _bytesInLastSecond, 0);
            CurrentUploadSpeedMbps = Math.Round((bytes * 8.0) / (1024.0 * 1024.0), 2); // Mbps
            OnSpeedUpdated?.Invoke(CurrentUploadSpeedMbps);
        };
        _speedTimer.Start();
    }

    /// <summary>
    /// מדווח על שליחת בייטים לצורך חישוב מהירות רשת רציף.
    /// </summary>
    public void ReportBytesSent(long count)
    {
        Interlocked.Add(ref _bytesInLastSecond, count);
        Interlocked.Add(ref _totalBytesTransferred, count);
    }

    /// <summary>
    /// רושם חיבור לקוח חדש שהחל בהורדה/צפייה.
    /// </summary>
    public ActiveClientConnection RegisterConnection(string clientIp, string fileName, long totalBytes)
    {
        var conn = new ActiveClientConnection
        {
            ClientIp = clientIp,
            TargetFileName = fileName,
            TotalBytes = totalBytes,
            CountryFlag = ResolveCountryFlag(clientIp)
        };

        _activeConnections[conn.ConnectionId] = conn;
        OnConnectionsChanged?.Invoke();
        return conn;
    }

    /// <summary>
    /// מעדכן התקדמות בייטים עבור חיבור פעיל.
    /// </summary>
    public void UpdateProgress(string connectionId, long bytesSent)
    {
        if (_activeConnections.TryGetValue(connectionId, out var conn))
        {
            conn.BytesTransferred += bytesSent;
            ReportBytesSent(bytesSent);
        }
    }

    /// <summary>
    /// מסיר חיבור שהסתיים.
    /// </summary>
    public void UnregisterConnection(string connectionId)
    {
        if (_activeConnections.TryRemove(connectionId, out _))
        {
            OnConnectionsChanged?.Invoke();
        }
    }

    /// <summary>
    /// מחזיר רשימה של כל החיבורים הפעילים ברגע זה.
    /// </summary>
    public List<ActiveClientConnection> GetActiveConnections() => _activeConnections.Values.ToList();

    /// <summary>
    /// מנתק חיבור פעיל מיידית לפי מזהה החיבור (Kill Connection).
    /// </summary>
    public void TerminateConnection(string connectionId)
    {
        if (_activeConnections.TryGetValue(connectionId, out var conn))
        {
            conn.Abort();
            _activeConnections.TryRemove(connectionId, out _);
            OnConnectionsChanged?.Invoke();
        }
    }

    private static string ResolveCountryFlag(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "WAN";
        if (ip == "127.0.0.1" || ip == "::1" || ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
        {
            return "LAN"; // רשת מקומית
        }
        return "WAN"; // אינטרנט / מנהור
    }
}
