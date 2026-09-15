using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// שירות סנכרון ומעקב אוטומטי אחרי תיקייה מוגדרת (Watch Folder).
/// כל קובץ שנשמר או מועבר לתיקייה מקבל אוטומטית קישור שיתוף,
/// הקישור מועתק ללוח, ונוצר קובץ מטא-דאטה '.share.txt' ליד הקובץ.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly string _folderPath;
    private readonly CancellationTokenSource _cts = new();

    public event Action<string, string>? OnFileSynced; // (filePath, shareUrl)

    public WatchFolderService(string folderPath)
    {
        _folderPath = folderPath;
        if (!Directory.Exists(_folderPath))
        {
            try
            {
                Directory.CreateDirectory(_folderPath);
            }
            catch { }
        }
    }

    /// <summary>
    /// מתחיל מעקב פעיל אחרי התיקייה.
    /// </summary>
    public void Start()
    {
        if (!Directory.Exists(_folderPath)) return;

        _watcher = new FileSystemWatcher(_folderPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = true
        };

        _watcher.Created += Watcher_OnFileCreated;
    }

    private void Watcher_OnFileCreated(object sender, FileSystemEventArgs e)
    {
        // התעלמות מקובצי מטא-דאטה או קבצים זמניים
        if (e.FullPath.EndsWith(".share.txt", StringComparison.OrdinalIgnoreCase) ||
            e.FullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            e.FullPath.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // בדיקת שחרור נעילת קובץ (File Lock Debounce) עד 15 שניות
            if (!await WaitForFileReadyAsync(e.FullPath, TimeSpan.FromSeconds(15)))
            {
                return;
            }

            try
            {
                // העלאה אוטומטית לענן ישיר
                string? shareUrl = await SecureTransferService.UploadToDirectPublicCloudAsync(e.FullPath);
                if (!string.IsNullOrEmpty(shareUrl))
                {
                    // יצירת קובץ מידע ליד הקובץ המקורי
                    string metaFile = e.FullPath + ".share.txt";
                    string metaContent = $"קישור שיתוף עבור: {Path.GetFileName(e.FullPath)}\r\n" +
                                         $"נוצר בתאריך: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                                         $"כתובת שיתוף: {shareUrl}\r\n\r\n" +
                                         $"(נבנה על ידי בינארי חכם: https://ivrit.smartbinary.org)";
                    await File.WriteAllTextAsync(metaFile, metaContent);

                    // העתקה אוטומטית ללוח (Clipboard) ב-STA Thread
                    var thread = new Thread(() =>
                    {
                        try { System.Windows.Clipboard.SetText(shareUrl); } catch { }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join(1000);

                    OnFileSynced?.Invoke(e.FullPath, shareUrl);
                }
            }
            catch { }
        });
    }

    private static async Task<bool> WaitForFileReadyAsync(string path, TimeSpan timeout)
    {
        var start = DateTime.Now;
        while (DateTime.Now - start < timeout)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (fs.Length > 0) return true;
            }
            catch (IOException)
            {
                await Task.Delay(400);
            }
        }
        return false;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        _cts.Cancel();
    }
}
