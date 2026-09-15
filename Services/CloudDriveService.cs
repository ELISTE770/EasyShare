using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// מידע על שירות ענן מזוהה במחשב המקומי (Google Drive, OneDrive וכד').
/// </summary>
public class CloudProviderInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string? LocalRootPath { get; set; }
    public bool IsAvailableLocally => !string.IsNullOrEmpty(LocalRootPath) && Directory.Exists(LocalRootPath);
    public string WebUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// שירות אינטגרציה חכם לשיתוף קבצים ותיקיות דרך Google Drive ו-Microsoft OneDrive.
/// מאתר את תיקיות הסנכרון המקומיות, מעביר פריטים לתיקיית שיתוף ייעודית, ומאפשר פתיחה מהירה בסייר וב-Web.
/// </summary>
public static class CloudDriveService
{
    /// <summary>
    /// מאתר את נתיב תיקיית הסנכרון המקומית של Microsoft OneDrive.
    /// </summary>
    public static CloudProviderInfo GetOneDriveInfo()
    {
        string? path = Environment.GetEnvironmentVariable("OneDriveCommercial");
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            path = Environment.GetEnvironmentVariable("OneDrive");
        }
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            path = Environment.GetEnvironmentVariable("OneDriveConsumer");
        }
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string defaultOd = Path.Combine(userProfile, "OneDrive");
            if (Directory.Exists(defaultOd)) path = defaultOd;
        }

        bool isLoc = !string.IsNullOrEmpty(path) && Directory.Exists(path);

        return new CloudProviderInfo
        {
            Id = "OneDrive",
            DisplayName = "Microsoft OneDrive",
            Icon = "⛅",
            LocalRootPath = isLoc ? path : null,
            WebUrl = "https://onedrive.live.com",
            Description = isLoc
                ? $"תיקייה מסונכרנת פעילה במחשב: {Path.GetFileName(path)}"
                : "שירות אחסון הענן של Microsoft (Web)"
        };
    }

    /// <summary>
    /// מאתר את נתיב הסנכרון של Google Drive (כונן וירטואלי G: או תיקייה מקומית).
    /// </summary>
    public static CloudProviderInfo GetGoogleDriveInfo()
    {
        string? path = null;

        // 1. בדיקת כוננים מקומיים (במיוחד G: או כונן עם תווית Google Drive)
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.IsReady && (drive.VolumeLabel.Contains("Google", StringComparison.OrdinalIgnoreCase) ||
                                      drive.Name.StartsWith("G:", StringComparison.OrdinalIgnoreCase)))
                {
                    // חיפוש תיקיית 'My Drive' או 'האחסון שלי'
                    string myDrive = Path.Combine(drive.RootDirectory.FullName, "My Drive");
                    if (Directory.Exists(myDrive))
                    {
                        path = myDrive;
                        break;
                    }

                    string hebrewDrive = Path.Combine(drive.RootDirectory.FullName, "האחסון שלי");
                    if (Directory.Exists(hebrewDrive))
                    {
                        path = hebrewDrive;
                        break;
                    }

                    path = drive.RootDirectory.FullName;
                    break;
                }
            }
        }
        catch { }

        // 2. בדיקת תיקייה מקומית בפרופיל המשתמש
        if (string.IsNullOrEmpty(path))
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string localGdrive = Path.Combine(userProfile, "Google Drive");
            if (Directory.Exists(localGdrive)) path = localGdrive;
        }

        bool isLoc = !string.IsNullOrEmpty(path) && Directory.Exists(path);

        return new CloudProviderInfo
        {
            Id = "GoogleDrive",
            DisplayName = "Google Drive",
            Icon = "📁",
            LocalRootPath = isLoc ? path : null,
            WebUrl = "https://drive.google.com/drive/my-drive",
            Description = isLoc
                ? $"כונן ענן מקומי מסונכרן: {path}"
                : "שירות אחסון הענן של Google (Web)"
        };
    }

    /// <summary>
    /// מעביר קובץ או תיקייה לתיקיית השיתוף של OneDrive ומכין אותם לשיתוף ענן.
    /// </summary>
    public static async Task<CloudShareResult> ShareViaOneDriveAsync(string sourcePath, CancellationToken ct = default)
    {
        var info = GetOneDriveInfo();
        return await ProcessCloudShareAsync(sourcePath, info, "שיתוף קל - OneDrive Shared", ct);
    }

    /// <summary>
    /// מעביר קובץ או תיקייה לתיקיית השיתוף של Google Drive ומכין אותם לשיתוף ענן.
    /// </summary>
    public static async Task<CloudShareResult> ShareViaGoogleDriveAsync(string sourcePath, CancellationToken ct = default)
    {
        var info = GetGoogleDriveInfo();
        return await ProcessCloudShareAsync(sourcePath, info, "שיתוף קל - Google Drive Shared", ct);
    }

    private static async Task<CloudShareResult> ProcessCloudShareAsync(
        string sourcePath,
        CloudProviderInfo provider,
        string targetSubfolderName,
        CancellationToken ct = default)
    {
        bool isDir = Directory.Exists(sourcePath);
        if (!isDir && !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("הקובץ או התיקייה המבוקשים אינם קיימים.", sourcePath);
        }

        string originalName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(originalName)) originalName = "SharedItem";

        if (provider.IsAvailableLocally && !string.IsNullOrEmpty(provider.LocalRootPath))
        {
            // הענן מסונכרן מקומית במחשב!
            string shareDir = Path.Combine(provider.LocalRootPath, targetSubfolderName);
            Directory.CreateDirectory(shareDir);

            string destPath;
            if (isDir)
            {
                // ארכוב תיקייה ל-ZIP ישירות בתוך תיקיית הענן
                destPath = Path.Combine(shareDir, $"{originalName}.zip");
                if (File.Exists(destPath)) File.Delete(destPath);
                ZipFile.CreateFromDirectory(sourcePath, destPath, CompressionLevel.Fastest, false);
            }
            else
            {
                // העתקת הקובץ
                destPath = Path.Combine(shareDir, originalName);
                await using (var srcStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                await using (var dstStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await srcStream.CopyToAsync(dstStream, ct);
                }
            }

            // פתיחת סייר הקבצים עם סימון הקובץ המסונכרן
            try
            {
                Process.Start("explorer.exe", $"/select,\"{destPath}\"");
            }
            catch { }

            return new CloudShareResult
            {
                Success = true,
                ProviderName = provider.DisplayName,
                IsLocalSync = true,
                SavedPath = destPath,
                WebUrl = provider.WebUrl,
                Message = $"הקובץ הועבר בהצלחה לתיקיית {provider.DisplayName}! הסנכרון לענן מתבצע כעת אוטומטית."
            };
        }
        else
        {
            // הענן אינו מותקן מקומית - מצב Web
            // נכין עותק מוכן בתיקיית Exports זמנית, נפתח את הסייר ואת דפדפן הענן
            string exportDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "שיתוף קל - מוכנים להעלאה");
            Directory.CreateDirectory(exportDir);

            string preparedPath;
            if (isDir)
            {
                preparedPath = Path.Combine(exportDir, $"{originalName}.zip");
                if (File.Exists(preparedPath)) File.Delete(preparedPath);
                ZipFile.CreateFromDirectory(sourcePath, preparedPath, CompressionLevel.Fastest, false);
            }
            else
            {
                preparedPath = Path.Combine(exportDir, originalName);
                File.Copy(sourcePath, preparedPath, true);
            }

            // העתקת הקובץ ישירות ללוח של Windows (Clipboard) כדי לאפשר הדבקה מיידית (Ctrl+V) ב-Drive
            try
            {
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var files = new System.Collections.Specialized.StringCollection { preparedPath };
                        System.Windows.Clipboard.SetFileDropList(files);
                    }
                    catch { }
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                thread.Join(1000);
            }
            catch { }

            // פתיחת הדפדפן לעמוד הענן
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = provider.WebUrl,
                    UseShellExecute = true
                });
            }
            catch { }

            // פתיחת הסייר עם סימון הקובץ המוכן
            try
            {
                Process.Start("explorer.exe", $"/select,\"{preparedPath}\"");
            }
            catch { }

            return new CloudShareResult
            {
                Success = true,
                ProviderName = provider.DisplayName,
                IsLocalSync = false,
                SavedPath = preparedPath,
                WebUrl = provider.WebUrl,
                Message = $"הקובץ הועתק אוטומטית ללוח (Clipboard) ודפדפן {provider.DisplayName} נפתח! כעת פשוט לחץ Ctrl+V בתוך חלון הדפדפן להעלאה מיידית (או גרור מהסייר שנפתח)."
            };
        }
    }
}

public class CloudShareResult
{
    public bool Success { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public bool IsLocalSync { get; set; }
    public string SavedPath { get; set; } = string.Empty;
    public string WebUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}
