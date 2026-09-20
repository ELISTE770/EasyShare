using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// תוצאת בדיקת עדכונים מ-GitHub Releases.
/// </summary>
public record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string ReleaseUrl,
    string? DownloadUrl,
    string? ErrorMessage = null
);

/// <summary>
/// שירות לבדיקת עדכונים אוטומטית ויזומה ממאגר GitHub של EasyShare PRO.
/// </summary>
public static class UpdateCheckerService
{
    public const string GitHubOwner = "ELISTE770";
    public const string GitHubRepo = "EasyShare";
    private const string ApiLatestReleaseUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public static readonly Version CurrentAppVersion = GetCurrentVersion();

    public static event Action<UpdateCheckResult>? OnUpdateDetected;

    private static Version GetCurrentVersion()
    {
        try
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var ver = asm.GetName().Version;
            if (ver != null)
            {
                return new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build));
            }
        }
        catch { }

        return new Version(2, 5, 0);
    }

    /// <summary>
    /// פונה ל-GitHub API ובודק האם קיים Release חדש יותר מהגרסה המותקנת.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        string currentVerStr = $"{CurrentAppVersion.Major}.{CurrentAppVersion.Minor}.{CurrentAppVersion.Build}";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"EasyShare-App/{currentVerStr} (Windows; SmartBinary)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");

            using var response = await client.GetAsync(ApiLatestReleaseUrl, ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new UpdateCheckResult(
                        HasUpdate: false,
                        CurrentVersion: currentVerStr,
                        LatestVersion: currentVerStr,
                        ReleaseTitle: string.Empty,
                        ReleaseNotes: string.Empty,
                        ReleaseUrl: $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases",
                        DownloadUrl: null,
                        ErrorMessage: "טרם פורסמו גרסאות רשמיות במאגר זה."
                    );
                }

                return new UpdateCheckResult(
                    HasUpdate: false,
                    CurrentVersion: currentVerStr,
                    LatestVersion: currentVerStr,
                    ReleaseTitle: string.Empty,
                    ReleaseNotes: string.Empty,
                    ReleaseUrl: $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases",
                    DownloadUrl: null,
                    ErrorMessage: $"GitHub API HTTP {(int)response.StatusCode}"
                );
            }

            string json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            string releaseTitle = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? tagName : tagName;
            string releaseNotes = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";
            string releaseUrl = root.TryGetProperty("html_url", out var htmlProp) 
                ? htmlProp.GetString() ?? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest" 
                : $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";

            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var aName) ? aName.GetString() ?? "" : "";
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (asset.TryGetProperty("browser_download_url", out var dlProp))
                        {
                            downloadUrl = dlProp.GetString();
                            break;
                        }
                    }
                }
            }

            string cleanTag = tagName.TrimStart('v', 'V').Trim();
            if (Version.TryParse(cleanTag, out var latestParsedVersion))
            {
                bool isNewer = latestParsedVersion > CurrentAppVersion;
                var result = new UpdateCheckResult(
                    HasUpdate: isNewer,
                    CurrentVersion: currentVerStr,
                    LatestVersion: cleanTag,
                    ReleaseTitle: releaseTitle,
                    ReleaseNotes: releaseNotes,
                    ReleaseUrl: releaseUrl,
                    DownloadUrl: downloadUrl
                );

                if (isNewer)
                {
                    OnUpdateDetected?.Invoke(result);
                }

                return result;
            }
            else
            {
                return new UpdateCheckResult(
                    HasUpdate: false,
                    CurrentVersion: currentVerStr,
                    LatestVersion: cleanTag,
                    ReleaseTitle: releaseTitle,
                    ReleaseNotes: releaseNotes,
                    ReleaseUrl: releaseUrl,
                    DownloadUrl: downloadUrl,
                    ErrorMessage: $"תגית גרסה לא מוכרת: {tagName}"
                );
            }
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(
                HasUpdate: false,
                CurrentVersion: currentVerStr,
                LatestVersion: currentVerStr,
                ReleaseTitle: string.Empty,
                ReleaseNotes: string.Empty,
                ReleaseUrl: $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases",
                DownloadUrl: null,
                ErrorMessage: "בדיקת העדכון בוטלה."
            );
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                HasUpdate: false,
                CurrentVersion: currentVerStr,
                LatestVersion: currentVerStr,
                ReleaseTitle: string.Empty,
                ReleaseNotes: string.Empty,
                ReleaseUrl: $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases",
                DownloadUrl: null,
                ErrorMessage: ex.Message
            );
        }
    }

    /// <summary>
    /// פתיחת דף ההורדה או ה-Release בדפדפן ברירת המחדל של המשתמש.
    /// </summary>
    public static void OpenReleaseInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(url) ? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest" : url,
                UseShellExecute = true
            });
        }
        catch { }
    }
}
