using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using EasyShare.Models;

namespace EasyShare.Services;

/// <summary>
/// מנהל את שמירת וטעינת ההגדרות לקובץ settings.json
/// ומספק אינטגרציה עם סל המחזור של Windows (Shell Recycle Bin).
/// </summary>
public sealed class SettingsService
{
    private static readonly string AppDirectory = AppContext.BaseDirectory;
    private static readonly string SettingsFilePath = Path.Combine(AppDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Current { get; private set; }

    public SettingsService()
    {
        Current = Load();
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded != null)
                {
                    Current = loaded;
                    return Current;
                }
            }
        }
        catch { }

        Current = new AppSettings();
        return Current;
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Current = settings;
            string json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(SettingsFilePath, json);
        }
        catch { }
    }

    public void Save() => Save(Current);

    #region אינטגרציה עם סל המחזור של Windows (Win32 SHFileOperation)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.U4)] public int wFunc;
        public string pFrom;
        public string pTo;
        public short fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const int FO_DELETE = 0x0003;
    private const int FOF_ALLOWUNDO = 0x0040;        // שמירה בסל המחזור
    private const int FOF_NOCONFIRMATION = 0x0010;   // ללא חלון אישור של Windows
    private const int FOF_SILENT = 0x0004;           // ללא דיאלוג התקדמות

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    /// <summary>
    /// מעביר קובץ או תיקייה אל סל המחזור של Windows (Recycle Bin) ללא מחיקה לצמיתות.
    /// אם הפעולה נכשלת או שהמערכת אינה Windows, מבוצעת מחיקה רגילה.
    /// </summary>
    public static bool MoveToRecycleBin(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // בלינוקס או מאק נבצע מחיקה רגילה
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, true);
            return true;
        }

        try
        {
            var shf = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = path + '\0' + '\0', // Win32 דורש סיום כפול של מחרוזת ב-\0
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
            };

            int result = SHFileOperation(ref shf);
            return result == 0 && !shf.fAnyOperationsAborted;
        }
        catch
        {
            // במקרה של כשל ב-Shell API, מחיקה ישירה
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
    #endregion
}
