using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace EasyShare.Services;

/// <summary>
/// אחראי על רישום והסרת תפריטי ההקשר של Windows Explorer (Shell Context Menu) ברמת המשתמש הנוכחי (ללא צורך ב-UAC).
/// מאפשר לחיצה ימנית על קובץ או תיקייה ובחירה בשיתוף מיידי.
/// </summary>
public static class ShellContextMenuService
{
    private const string MenuKeyName = "EasySharePRO";
    private const string MenuTitle = "שתף באמצעות EasyShare PRO";

    /// <summary>
    /// בודק האם תפריט ההקשר רשום כעת ברגיסטרי.
    /// </summary>
    public static bool IsContextMenuRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\" + MenuKeyName);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// רושם תפריט הקשר מקונן (Cascading Menu) עבור קבצים ותיקיות.
    /// </summary>
    public static void RegisterContextMenu()
    {
        string exePath = Process.GetCurrentProcess().MainModule?.FileName 
            ?? Path.Combine(AppContext.BaseDirectory, "EasyShare.exe");

        // 1. רישום עבור כל הקבצים (*)
        RegisterForTarget(@"Software\Classes\*\shell", exePath);

        // 2. רישום עבור תיקיות (Directory)
        RegisterForTarget(@"Software\Classes\Directory\shell", exePath);
    }

    private static void RegisterForTarget(string baseKeyPath, string exePath)
    {
        using var baseKey = Registry.CurrentUser.CreateSubKey(baseKeyPath);
        if (baseKey == null) return;

        using var easyKey = baseKey.CreateSubKey(MenuKeyName);
        if (easyKey == null) return;

        easyKey.SetValue("", MenuTitle);
        easyKey.SetValue("Icon", $"\"{exePath}\",0");
        easyKey.SetValue("SubCommands", "");

        using var shellSub = easyKey.CreateSubKey("shell");
        if (shellSub == null) return;

        // אפשרות 1: העלאה ישירה לענן מהיר (קישור ציבורי ישיר)
        using (var cmdCloud = shellSub.CreateSubKey("01_DirectCloud"))
        {
            if (cmdCloud != null)
            {
                cmdCloud.SetValue("", "העלאה מהירה לענן (קישור ישיר)");
                using var cmd = cmdCloud.CreateSubKey("command");
                cmd?.SetValue("", $"\"{exePath}\" --quick-share \"DirectCloud\" \"%1\"");
            }
        }

        // אפשרות 2: שיתוף מאובטח עם PIN (לוקאלי / מנהור)
        using (var cmdSecure = shellSub.CreateSubKey("02_SecurePin"))
        {
            if (cmdSecure != null)
            {
                cmdSecure.SetValue("", "שיתוף מאובטח עם אימות PIN");
                using var cmd = cmdSecure.CreateSubKey("command");
                cmd?.SetValue("", $"\"{exePath}\" --quick-share \"Tunnel\" \"%1\"");
            }
        }

        // אפשרות 3: סנכרון והעברה לענן מקומי (Google Drive / OneDrive)
        using (var cmdDrive = shellSub.CreateSubKey("03_CloudDrive"))
        {
            if (cmdDrive != null)
            {
                cmdDrive.SetValue("", "סנכרון לתיקיית ענן (Google Drive / OneDrive)");
                using var cmd = cmdDrive.CreateSubKey("command");
                cmd?.SetValue("", $"\"{exePath}\" --quick-share \"LocalCloud\" \"%1\"");
            }
        }
    }

    /// <summary>
    /// מסיר את תפריט ההקשר מהרגיסטרי.
    /// </summary>
    public static void UnregisterContextMenu()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\" + MenuKeyName, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\" + MenuKeyName, false);
        }
        catch { }
    }
}
