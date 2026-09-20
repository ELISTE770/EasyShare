using System;
using EasyShare.Core;

namespace EasyShare.Models;

/// <summary>
/// מייצג הודעת צ'אט ישירה בין עמיתים (כולל תמיכה בקבצים מצורפים והיסטוריית שיחה).
/// נבנה על ידי בינארי חכם - https://ivrit.smartbinary.org
/// </summary>
public class PeerChatMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string SenderPeerId { get; set; } = "";
    public string SenderName { get; set; } = "";
    public string RecipientPeerId { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsOutgoing { get; set; }

    public string TimeFormatted => Timestamp.ToString("HH:mm");

    public bool HasAttachment => !string.IsNullOrEmpty(AttachedFileName);
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public string? AttachedFileName { get; set; }
    public string? AttachedFilePath { get; set; }
    public long AttachedFileSize { get; set; }
    public string AttachedFileSizeFormatted => FormatBytes(AttachedFileSize);

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double val = bytes;
        while (val >= 1024.0 && i < units.Length - 1)
        {
            val /= 1024.0;
            i++;
        }
        return $"{val:0.##} {units[i]}";
    }

    public string DisplaySender => IsOutgoing ? "אני" : (string.IsNullOrWhiteSpace(SenderName) ? SenderPeerId : SenderName);
}
