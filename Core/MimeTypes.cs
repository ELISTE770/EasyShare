using System;
using System.Collections.Generic;
using System.IO;

namespace EasyShare.Core;

/// <summary>
/// מספק מיפוי מדויק של סיומות קבצים ל-MIME Types
/// בדגש על קובצי מדיה להזרמת אודיו ווידאו (HTTP 206 Streaming).
/// </summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        // וידאו
        { ".mp4", "video/mp4" },
        { ".m4v", "video/mp4" },
        { ".mkv", "video/x-matroska" },
        { ".webm", "video/webm" },
        { ".mov", "video/quicktime" },
        { ".avi", "video/x-msvideo" },
        { ".wmv", "video/x-ms-wmv" },
        { ".flv", "video/x-flv" },
        { ".ts", "video/mp2t" },
        { ".3gp", "video/3gpp" },

        // אודיו
        { ".mp3", "audio/mpeg" },
        { ".wav", "audio/wav" },
        { ".flac", "audio/flac" },
        { ".m4a", "audio/mp4" },
        { ".aac", "audio/aac" },
        { ".ogg", "audio/ogg" },
        { ".oga", "audio/ogg" },
        { ".opus", "audio/opus" },
        { ".wma", "audio/x-ms-wma" },
        { ".weba", "audio/webm" },

        // תמונות
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".png", "image/png" },
        { ".gif", "image/gif" },
        { ".webp", "image/webp" },
        { ".svg", "image/svg+xml" },
        { ".bmp", "image/bmp" },
        { ".ico", "image/x-icon" },
        { ".tiff", "image/tiff" },
        { ".tif", "image/tiff" },

        // מסמכים וטקסט
        { ".pdf", "application/pdf" },
        { ".txt", "text/plain; charset=utf-8" },
        { ".csv", "text/csv; charset=utf-8" },
        { ".json", "application/json; charset=utf-8" },
        { ".xml", "application/xml; charset=utf-8" },
        { ".html", "text/html; charset=utf-8" },
        { ".htm", "text/html; charset=utf-8" },
        { ".css", "text/css; charset=utf-8" },
        { ".js", "application/javascript; charset=utf-8" },
        { ".md", "text/markdown; charset=utf-8" },
        { ".doc", "application/msword" },
        { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
        { ".xls", "application/vnd.ms-excel" },
        { ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" },
        { ".ppt", "application/vnd.ms-powerpoint" },
        { ".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation" },

        // ארכיונים
        { ".zip", "application/zip" },
        { ".rar", "application/vnd.rar" },
        { ".7z", "application/x-7z-compressed" },
        { ".tar", "application/x-tar" },
        { ".gz", "application/gzip" },

        // קבצי הפעלה ובינאריים
        { ".exe", "application/vnd.microsoft.portable-executable" },
        { ".iso", "application/x-iso9660-image" },
        { ".apk", "application/vnd.android.package-archive" }
    };

    /// <summary>
    /// מחזיר את ה-MimeType של קובץ לפי סיומתו.
    /// ברירת המחדל לקבצים לא מוכרים היא application/octet-stream.
    /// </summary>
    public static string GetMimeType(string filePathOrExtension)
    {
        string ext = Path.GetExtension(filePathOrExtension);
        if (string.IsNullOrEmpty(ext))
        {
            return "application/octet-stream";
        }

        return Types.TryGetValue(ext, out string? mime) ? mime : "application/octet-stream";
    }

    /// <summary>
    /// בודק האם הקובץ הוא קובץ מדיה (אודיו או וידאו)
    /// </summary>
    public static bool IsMediaFile(string filePath)
    {
        string mime = GetMimeType(filePath);
        return mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
               mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// בודק האם הקובץ הוא תמונה
    /// </summary>
    public static bool IsImageFile(string filePath)
    {
        string mime = GetMimeType(filePath);
        return mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    }
}
