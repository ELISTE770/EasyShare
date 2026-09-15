using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace EasyShare.Web;

/// <summary>
/// אחראי על טעינת משאבי ה-Web המוטמעים (Embedded Resources) ישירות מתוך ה-Assembly
/// עם תמיכה ב-Hot Reload מפיתוח מקומי במידה והקובץ קיים בדיסק.
/// </summary>
public static class EmbeddedResources
{
    private static string? _cachedIndexHtml;

    /// <summary>
    /// מחזיר את תוכן דף ה-SPA הראשי (index.html).
    /// </summary>
    public static string GetIndexHtml()
    {
        // 1. בדיקה האם הקובץ קיים פיזית בדיסק המקומי (נוח בזמן פיתוח)
        string localPath = Path.Combine(AppContext.BaseDirectory, "Web", "Assets", "index.html");
        if (File.Exists(localPath))
        {
            return File.ReadAllText(localPath, Encoding.UTF8);
        }

        if (_cachedIndexHtml != null)
        {
            return _cachedIndexHtml;
        }

        // 2. טעינה מתוך המשאבים המוטמעים ב-Assembly
        var assembly = Assembly.GetExecutingAssembly();
        string[] resourceNames = assembly.GetManifestResourceNames();

        foreach (var name in resourceNames)
        {
            if (name.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    _cachedIndexHtml = reader.ReadToEnd();
                    return _cachedIndexHtml;
                }
            }
        }

        // במקרה קיצון שלא נמצא המשאב
        return "<!DOCTYPE html><html><body><h1>EasyShare</h1><p>index.html resource not found.</p></body></html>";
    }

    /// <summary>
    /// מחזיר את תוכן קובץ ה-PWA manifest.
    /// </summary>
    public static string GetManifestJson()
    {
        string localPath = Path.Combine(AppContext.BaseDirectory, "Web", "Assets", "manifest.webmanifest");
        if (File.Exists(localPath))
        {
            return File.ReadAllText(localPath, Encoding.UTF8);
        }

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith("manifest.webmanifest", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return reader.ReadToEnd();
                }
            }
        }
        return "{}";
    }

    /// <summary>
    /// מחזיר את תוכן ה-Service Worker (sw.js).
    /// </summary>
    public static string GetServiceWorkerJs()
    {
        string localPath = Path.Combine(AppContext.BaseDirectory, "Web", "Assets", "sw.js");
        if (File.Exists(localPath))
        {
            return File.ReadAllText(localPath, Encoding.UTF8);
        }

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith("sw.js", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return reader.ReadToEnd();
                }
            }
        }
        return "// Empty SW";
    }

    /// <summary>
    /// מחזיר את הבתים הגולמיים של נכס סטטי מתיקיית Web/Assets (למשל צלמיות או תמונות).
    /// </summary>
    public static byte[]? GetAssetBytes(string filename)
    {
        string localPath = Path.Combine(AppContext.BaseDirectory, "Web", "Assets", filename);
        if (File.Exists(localPath))
        {
            return File.ReadAllBytes(localPath);
        }

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith(filename, StringComparison.OrdinalIgnoreCase))
            {
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }
        return null;
    }
}
