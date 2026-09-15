using System;
using System.IO;

namespace EasyShare.Models;

/// <summary>
/// הגדרות המערכת והשרת - כולל הגדרות מתקדמות לתצוגה, אבטחה, מצב גישה וסל מחזור.
/// </summary>
public class AppSettings
{
    #region 1. הגדרות שרת ורשת (Server & Network)
    /// <summary>
    /// יציאת השרת (Port). ברירת מחדל: 2121
    /// </summary>
    public int ServerPort { get; set; } = 2121;

    /// <summary>
    /// מצב גישה: "FullComputer" (גישה לכל הכוננים והתיקיות המהירות) או "SingleFolder" (תיקייה מוגדרת בלבד)
    /// </summary>
    public string AccessMode { get; set; } = "FullComputer";

    /// <summary>
    /// תיקיית השיתוף הייעודית במידה ונבחר SingleFolder
    /// </summary>
    public string SharedFolderPath { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// הרשאת קריאה בלבד (חוסם העלאה, מחיקה, שינוי שם ויצירת תיקיות)
    /// </summary>
    public bool ServerReadOnly { get; set; } = false;

    /// <summary>
    /// האם לאפשר מנהור מאובטח דרך Cloudflare Tunnels
    /// </summary>
    public bool EnableCloudflareTunnel { get; set; } = false;

    /// <summary>
    /// סוג המנהור: "Random" (Quick Tunnel אקראי) או "CustomDomain" (דומיין אישי קבוע)
    /// </summary>
    public string TunnelMode { get; set; } = "Random";

    /// <summary>
    /// מפתח Token של Cloudflare Tunnel (מ-Zero Trust Dashboard)
    /// </summary>
    public string? CloudflareTunnelToken { get; set; } = null;

    /// <summary>
    /// כתובת הדומיין המותאם אישית (למשל: https://share.smartbinary.org)
    /// </summary>
    public string? CloudflareCustomDomain { get; set; } = null;
    #endregion

    #region 2. אבטחה ואימות (Security & Authentication)
    /// <summary>
    /// גישה אנונימית (ללא צורך בשם משתמש וסיסמה)
    /// </summary>
    public bool ServerAnonymous { get; set; } = true;

    /// <summary>
    /// שם משתמש ל-Basic Auth (במידה ואינו אנונימי)
    /// </summary>
    public string ServerUsername { get; set; } = "pc";

    /// <summary>
    /// סיסמה ל-Basic Auth
    /// </summary>
    public string ServerPassword { get; set; } = string.Empty;

    /// <summary>
    /// קוד PIN בן 8 ספרות לאבטחה נקודתית (אם ריק - אין דרישת PIN)
    /// </summary>
    public string? SecurityPin { get; set; } = null;
    #endregion

    #region 3. סייר קבצים וסל מחזור (Explorer & Recycle Bin)
    /// <summary>
    /// העברת קבצים שנמחקים אל סל המחזור של Windows במקום מחיקה לצמיתות
    /// </summary>
    public bool SendToRecycleBin { get; set; } = true;

    /// <summary>
    /// הצגת קבצים ותיקיות מוסתרים
    /// </summary>
    public bool ShowHiddenFiles { get; set; } = false;

    /// <summary>
    /// הצגת תיקיות תמיד בראש הרשימה
    /// </summary>
    public bool FoldersFirst { get; set; } = true;

    /// <summary>
    /// תצוגת ברירת מחדל: "Grid" (רשת כרטיסים) או "List" (רשימה מפורטת)
    /// </summary>
    public string DefaultViewMode { get; set; } = "Grid";

    /// <summary>
    /// שדה מיון: "Name", "Date", "Size", "Type"
    /// </summary>
    public string SortField { get; set; } = "Name";

    /// <summary>
    /// כיוון מיון: true = עולה (A-Z), false = יורד (Z-A)
    /// </summary>
    public bool SortAscending { get; set; } = true;
    #endregion

    #region 4. תצוגה ועיצוב (Theme & UX)
    /// <summary>
    /// ערכת נושא: "dark" או "light"
    /// </summary>
    public string ThemeMode { get; set; } = "dark";

    /// <summary>
    /// שפת ממשק: "he" או "en"
    /// </summary>
    public string Language { get; set; } = "he";
    #endregion

    #region 5. אינטגרציית שולחן עבודה ואוטומציה (Desktop & Windows Integration)
    /// <summary>
    /// האם לאפשר קיצור מקשים גלובלי (Alt+S) לשיתוף מהיר
    /// </summary>
    public bool EnableGlobalHotkey { get; set; } = true;

    /// <summary>
    /// האם להציג ווידג'ט צף שולחני (Floating Drop-Zone)
    /// </summary>
    public bool EnableFloatingDropZone { get; set; } = false;

    /// <summary>
    /// האם לאפשר מעקב אחרי תיקיית סנכרון אוטומטית (Watch Folder)
    /// </summary>
    public bool EnableWatchFolder { get; set; } = false;

    /// <summary>
    /// נתיב תיקיית הסנכרון האוטומטית
    /// </summary>
    public string WatchFolderPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "EasyShare_Sync");

    /// <summary>
    /// האם להפעיל mDNS לזיהוי ברשת המקומית (easyshare.local)
    /// </summary>
    public bool EnableMdns { get; set; } = true;

    /// <summary>
    /// האם לנסות לפתוח פורט בראוטר דרך UPnP (ברירת מחדל: כבוי, דורש הפעלה יזומה)
    /// </summary>
    public bool EnableUpnp { get; set; } = false;

    /// <summary>
    /// פעולה בעת לחיצה על כפתור הסגירה (X): "MinimizeToTray" (מזעור למגש המערכת) או "ExitApplication" (סגירה מלאה ויציאה)
    /// </summary>
    public string CloseAction { get; set; } = "MinimizeToTray";
    #endregion
}
