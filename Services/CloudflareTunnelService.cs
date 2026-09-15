using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// מנהל מנהור מאובטח וחיצוני דרך Cloudflare Quick Tunnels (trycloudflare.com).
/// מאפשר גישה מכל מקום בעולם דרך HTTPS ללא צורך ב-Port Forwarding או IP סטטי.
/// </summary>
public sealed class CloudflareTunnelService : IDisposable
{
    private Process? _process;
    private readonly object _lock = new();
    private static readonly Regex UrlRegex = new(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);

    public event Action<string>? OnUrlChanged;
    public event Action<string>? OnStateChanged;
    public event Action<string>? OnError;

    public string? CurrentUrl { get; private set; }
    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// מאתר את קובץ ההפעלה של cloudflared או מחזיר את הנתיב המקומי הצפוי שלו.
    /// </summary>
    public static string GetCloudflaredPath()
    {
        string localExe = Path.Combine(AppContext.BaseDirectory, "cloudflared.exe");
        if (File.Exists(localExe)) return localExe;

        string appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyShare", "cloudflared.exe");
        if (File.Exists(appDataPath)) return appDataPath;

        return appDataPath;
    }

    /// <summary>
    /// בודק האם cloudflared מותקן, ובמידה ולא - מעתיק ממאגר מקומי או מוריד את הגרסה הרשמית של Cloudflare.
    /// </summary>
    public async Task<bool> EnsureCloudflaredInstalledAsync(Action<string>? progressLogger = null, CancellationToken ct = default)
    {
        string exePath = GetCloudflaredPath();
        if (File.Exists(exePath))
        {
            return true;
        }

        // ניסיון איתור ממיקומים מקומיים
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "cloudflared.exe")
        ];

        foreach (var cand in candidates)
        {
            if (File.Exists(cand))
            {
                try
                {
                    string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyShare", "cloudflared.exe");
                    string dir = Path.GetDirectoryName(target)!;
                    Directory.CreateDirectory(dir);
                    File.Copy(cand, target, true);
                    progressLogger?.Invoke($"Cloudflared הועתק בהצלחה אל: {target}");
                    return true;
                }
                catch { }
            }
        }

        try
        {
            string dir = Path.GetDirectoryName(exePath)!;
            Directory.CreateDirectory(dir);

            progressLogger?.Invoke("Cloudflared not found. Downloading official executable from Cloudflare GitHub...");
            string downloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("EasyShare-Agent/1.0");

            byte[] data = await httpClient.GetByteArrayAsync(downloadUrl, ct);
            await File.WriteAllBytesAsync(exePath, data, ct);

            progressLogger?.Invoke($"Cloudflared successfully downloaded to: {exePath}");
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to download cloudflared: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// מפעיל את מנהור ה-Cloudflare עבור פורט ה-TCP המקומי (מנהור אקראי רגיל).
    /// </summary>
    public Task<bool> StartAsync(int localPort, Action<string>? logger = null, CancellationToken ct = default)
    {
        return StartAsync(localPort, "Random", null, null, logger, ct);
    }

    /// <summary>
    /// מפעיל את מנהור ה-Cloudflare במצב אקראי (Quick Tunnel) או במצב דומיין אישי מותאם (Custom Domain עם Token).
    /// </summary>
    public async Task<bool> StartAsync(
        int localPort,
        string tunnelMode,
        string? token,
        string? customDomain,
        Action<string>? logger = null,
        CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (IsRunning) return true;
        }

        bool installed = await EnsureCloudflaredInstalledAsync(logger, ct);
        if (!installed)
        {
            OnError?.Invoke("Cannot start Cloudflare Tunnel: cloudflared.exe is missing.");
            return false;
        }

        string exePath = GetCloudflaredPath();
        bool isCustom = string.Equals(tunnelMode, "CustomDomain", StringComparison.OrdinalIgnoreCase);

        string arguments;
        if (isCustom)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                OnError?.Invoke("טוקן מנהור (Tunnel Token) חסר. אנא הזן טוקן מלוח הניהול של Cloudflare Zero Trust.");
                return false;
            }

            // הרצת מנהור מנוהל מול Cloudflare Zero Trust באמצעות Token
            arguments = $"tunnel --no-autoupdate run --token {token.Trim()}";

            // קביעת כתובת ה-URL הציבורית לפי הדומיין שהוגדר
            if (!string.IsNullOrWhiteSpace(customDomain))
            {
                string cleanDomain = customDomain.Trim();
                if (!cleanDomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !cleanDomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    cleanDomain = $"https://{cleanDomain}";
                }
                CurrentUrl = cleanDomain;
            }
            OnStateChanged?.Invoke($"Starting Custom Domain Cloudflare Tunnel ({CurrentUrl ?? "Domain configured in Zero Trust"})...");
        }
        else
        {
            // מנהור אקראי מהיר (Quick Tunnel trycloudflare.com)
            arguments = $"tunnel --url http://127.0.0.1:{localPort} --no-autoupdate";
            OnStateChanged?.Invoke("Starting Cloudflare Quick Tunnel (trycloudflare.com)...");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            var proc = new Process { StartInfo = startInfo };
            proc.OutputDataReceived += (_, e) => ProcessOutput(e.Data, isCustom);
            proc.ErrorDataReceived += (_, e) => ProcessOutput(e.Data, isCustom);

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (_lock)
            {
                _process = proc;
            }

            if (isCustom && !string.IsNullOrEmpty(CurrentUrl))
            {
                OnUrlChanged?.Invoke(CurrentUrl);
                OnStateChanged?.Invoke($"מנהור עם דומיין מותאם אישית פעיל: {CurrentUrl}");
            }
            else
            {
                OnStateChanged?.Invoke("Tunnel process launched, waiting for public URL...");
            }
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Error starting cloudflared process: {ex.Message}");
            return false;
        }
    }

    private void ProcessOutput(string? data, bool isCustom = false)
    {
        if (string.IsNullOrEmpty(data)) return;

        if (!isCustom)
        {
            var match = UrlRegex.Match(data);
            if (match.Success)
            {
                CurrentUrl = match.Value;
                OnUrlChanged?.Invoke(CurrentUrl);
                OnStateChanged?.Invoke($"Tunnel active: {CurrentUrl}");
            }
        }
        else
        {
            // זיהוי רישום מוצלח מול שרתי Cloudflare במצב Custom Domain
            if (data.Contains("Registered tunnel connection", StringComparison.OrdinalIgnoreCase) ||
                data.Contains("Connection registered", StringComparison.OrdinalIgnoreCase))
            {
                OnStateChanged?.Invoke($"חיבור המנהור נוצר בהצלחה מול שרתי Cloudflare! ({CurrentUrl})");
            }
        }
    }

    /// <summary>
    /// עוצר את פעילות המנהור ומחסל את התהליך.
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit(2000);
                    }
                }
                catch
                {
                    // תהליך כבר נעצר
                }
                finally
                {
                    _process.Dispose();
                    _process = null;
                    CurrentUrl = null;
                    OnStateChanged?.Invoke("Tunnel stopped.");
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
